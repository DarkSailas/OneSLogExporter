using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OneSLogExporter.Core.Parsers;

/// <summary>
/// Потоковое чтение записей журнала из JSON-файла произвольного вида:
/// построчный JSON (NDJSON / JSON Lines), объекты подряд, массив объектов (в одну строку или с отступами),
/// объект-обёртка с массивом внутри (например <c>{"data":[...]}</c> или <c>hits.hits[]._source</c> из Elasticsearch).
/// Файл не загружается в память целиком: в буфере держится только текущая запись.
/// </summary>
internal static class JsonDumpReader
{
    private const int FileBufferSize = 65536;
    private const int InitialBufferSize = 64 * 1024;
    private const int MinFreeSpace = 16 * 1024;

    // Объект крупнее этого предела записью журнала быть не может — его накопление прекращается.
    private const int MaxRecordBytes = 64 * 1024 * 1024;

    public static async IAsyncEnumerable<T> ReadFileAsync<T>(
        string jsonFilePath,
        long startOffset,
        JsonTypeInfo<T> typeInfo,
        [EnumeratorCancellation] CancellationToken ct = default) where T : class
    {
        if (!File.Exists(jsonFilePath)) yield break;

        await using var file = new FileStream(
            jsonFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            FileBufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        var utf16 = await DetectUtf16Async(file, ct).ConfigureAwait(false);
        if (utf16 is not null)
        {
            // Кодовая единица UTF-16 занимает два байта: нечётное смещение сдвинуло бы весь текст.
            startOffset &= ~1L;
        }
        file.Seek(startOffset > 0 && startOffset < file.Length ? startOffset : 0, SeekOrigin.Begin);

        await using var source = utf16 is null
            ? file
            : Encoding.CreateTranscodingStream(file, utf16, Encoding.UTF8, leaveOpen: true);

        using var scanner = new Scanner<T>(typeInfo);
        List<T> found = [];

        while (true)
        {
            var read = await source.ReadAsync(scanner.GetFreeSpace(), ct).ConfigureAwait(false);
            if (read == 0) break;

            scanner.Advance(read, found);

            foreach (var doc in found)
            {
                yield return doc;
            }
            found.Clear();
        }
    }

    /// <summary>
    /// UTF-16 распознаётся по метке порядка байтов (так сохраняет вывод Windows PowerShell 5.1); всё остальное читается как UTF-8.
    /// </summary>
    private static async ValueTask<Encoding?> DetectUtf16Async(FileStream file, CancellationToken ct)
    {
        var bom = new byte[2];
        var total = 0;
        while (total < bom.Length)
        {
            var read = await file.ReadAsync(bom.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        if (total < 2) return null;
        if (bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return null;
    }

    private static bool IsBadRecord(Exception ex) =>
        ex is JsonException or FormatException or InvalidOperationException or NotSupportedException;

    /// <summary>
    /// Выделяет из потока байтов UTF-8 объекты-кандидаты в записи: объект верхнего уровня либо элемент массива.
    /// Проверка идёт от вложенных объектов к внешним: если записью оказался элемент массива, объемлющий объект — обёртка;
    /// если нет, записью может быть сам объемлющий объект (у него просто есть поле с массивом объектов).
    /// </summary>
    private sealed class Scanner<T>(JsonTypeInfo<T> typeInfo) : IDisposable where T : class
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        private bool[] _isArray = new bool[32];
        private int _depth;
        private int _end;

        // Незакрытые кандидаты от внешнего к вложенному: позиция открывающей скобки в буфере и глубина, на которой он открыт.
        private int[] _starts = new int[8];
        private int[] _depths = new int[8];
        private int _candidates;

        private bool _inString;
        private bool _escaped;
        private bool _atLineStart = true;
        private byte _lastSignificant;
        private bool _nestedFirst;

        public Memory<byte> GetFreeSpace()
        {
            if (_candidates == 0)
            {
                _end = 0;
            }
            else if (_starts[0] > 0)
            {
                var shift = _starts[0];
                _buffer.AsSpan(shift, _end - shift).CopyTo(_buffer);
                _end -= shift;
                for (var c = 0; c < _candidates; c++)
                {
                    _starts[c] -= shift;
                }
            }

            if (_buffer.Length - _end < MinFreeSpace)
            {
                if (_buffer.Length >= MaxRecordBytes)
                {
                    _candidates = 0;
                    _end = 0;
                }
                else
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                    _buffer.AsSpan(0, _end).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = bigger;
                }
            }

            return _buffer.AsMemory(_end);
        }

        public void Advance(int count, List<T> output)
        {
            var data = _buffer.AsSpan(0, _end + count);

            for (var i = _end; i < data.Length; i++)
            {
                var b = data[i];

                if (_inString)
                {
                    if (b == (byte)'\n')
                    {
                        // В корректном JSON перевода строки внутри строки нет: запись оборвана, начинаем заново.
                        _inString = false;
                        _escaped = false;
                        _depth = 0;
                        _candidates = 0;
                        _atLineStart = true;
                        _lastSignificant = 0;
                    }
                    else if (_escaped)
                    {
                        _escaped = false;
                    }
                    else if (b == (byte)'\\')
                    {
                        _escaped = true;
                    }
                    else if (b == (byte)'"')
                    {
                        _inString = false;
                    }
                    continue;
                }

                switch (b)
                {
                    case (byte)'"':
                        _inString = true;
                        break;

                    case (byte)'{':
                        // Объект с первой позиции строки при незакрытой записи верхнего уровня: предыдущая строка NDJSON оборвана.
                        // После «:», «,» и «[» объект ожидаем: это либо форматированный текст со скобкой на отдельной строке,
                        // либо обрыв пришёлся ровно на это место. Тогда объект проверяется как запись при закрытии.
                        var afterLineBreak = _atLineStart && _candidates > 0 && _depths[0] == 0;
                        if (afterLineBreak && _lastSignificant is not ((byte)':' or (byte)',' or (byte)'['))
                        {
                            _depth = 0;
                            _candidates = 0;
                        }
                        if (_depth == 0 || _isArray[_depth - 1] || afterLineBreak)
                        {
                            PushCandidate(i);
                        }
                        Push(isArray: false);
                        break;

                    case (byte)'[':
                        Push(isArray: true);
                        break;

                    case (byte)'}':
                    case (byte)']':
                        if (_depth == 0) break;
                        _depth--;
                        while (_candidates > 0 && _depths[_candidates - 1] > _depth)
                        {
                            _candidates--;
                        }
                        if (_candidates > 0 && _depths[_candidates - 1] == _depth)
                        {
                            _candidates--;
                            // Запись найдена — значит, все объемлющие объекты были обёртками.
                            if (b == (byte)'}' && TryAdd(data[_starts[_candidates]..(i + 1)], output))
                            {
                                _candidates = 0;
                                if (_depth > 0 && !_isArray[_depth - 1])
                                {
                                    // Запись стояла на месте значения свойства: объемлющая строка была оборвана.
                                    _depth = 0;
                                }
                            }
                        }
                        break;
                }

                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    _lastSignificant = b;
                }
                _atLineStart = b == (byte)'\n';
            }

            _end = data.Length;
        }

        private void Push(bool isArray)
        {
            if (_depth == _isArray.Length)
            {
                Array.Resize(ref _isArray, _isArray.Length * 2);
            }
            _isArray[_depth++] = isArray;
        }

        private void PushCandidate(int start)
        {
            if (_candidates == _starts.Length)
            {
                Array.Resize(ref _starts, _starts.Length * 2);
                Array.Resize(ref _depths, _depths.Length * 2);
            }
            _starts[_candidates] = start;
            _depths[_candidates++] = _depth;
        }

        private bool TryAdd(ReadOnlySpan<byte> json, List<T> output)
        {
            T? doc;
            if (_nestedFirst)
            {
                // В этом файле записи уже находились во вложенном объекте — начинаем с него.
                doc = DeserializeNested(json) ?? Deserialize(json);
            }
            else
            {
                doc = Deserialize(json);
                if (doc is null && (doc = DeserializeNested(json)) is not null)
                {
                    _nestedFirst = true;
                }
            }

            if (doc is null) return false;

            output.Add(doc);
            return true;
        }

        private T? Deserialize(ReadOnlySpan<byte> json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, typeInfo);
            }
            catch (Exception ex) when (IsBadRecord(ex))
            {
                return null;
            }
        }

        /// <summary>
        /// Запись может лежать на уровень глубже, во вложенном объекте (например <c>_source</c> в выдаче Elasticsearch).
        /// </summary>
        private T? DeserializeNested(ReadOnlySpan<byte> json)
        {
            try
            {
                var reader = new Utf8JsonReader(json);
                using var document = JsonDocument.ParseValue(ref reader);

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object) continue;

                    try
                    {
                        if (property.Value.Deserialize(typeInfo) is { } doc) return doc;
                    }
                    catch (Exception ex) when (IsBadRecord(ex))
                    {
                    }
                }
            }
            catch (JsonException)
            {
            }

            return null;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }
}
