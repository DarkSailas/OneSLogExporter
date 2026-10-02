using System.Text;

namespace OneSLogExporter.Core.Parsers;

/// <summary>
/// Высокопроизводительный потоковый построчный ридер с точным отслеживанием байтового смещения начала каждой строки.
/// Исключает рассинхронизацию смещений при чтении активных логов 1С и JSON-дампов.
/// </summary>
public sealed class FastLogLineReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _bufferPos;
    private int _bufferLen;
    private long _streamReadOffset;
    private bool _disposed;

    /// <summary>
    /// Точное физическое смещение в байтах в исходном потоке/файле, с которого начинается текущая вычитанная строка.
    /// </summary>
    public long CurrentLineStartOffset { get; private set; }

    public FastLogLineReader(Stream stream, int bufferSize = 65536)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
        _streamReadOffset = stream.Position;
        CurrentLineStartOffset = _streamReadOffset;
    }

    /// <summary>
    /// Смещение в байтах, с которого начнётся следующая строка (сразу после '\n' последней вычитанной строки).
    /// </summary>
    public long NextLineStartOffset => _streamReadOffset - (_bufferLen - _bufferPos);

    /// <summary>
    /// true, если последняя вычитанная строка закончилась '\n'; false — строка оборвана концом файла (возможно, дописывается).
    /// </summary>
    public bool LastLineTerminated { get; private set; } = true;

    /// <summary>
    /// Чтение следующей строки UTF-8 с фиксацией точного байтового смещения её начала.
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct = default)
    {
        if (_disposed) return null;

        // Байты длинной строки копятся целиком: декодирование по кускам рвёт многобайтовые символы на стыке буферов.
        List<byte>? longLineBytes = null;
        var recordedLineStart = false;

        while (true)
        {
            if (_bufferPos >= _bufferLen)
            {
                _bufferPos = 0;
                _bufferLen = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), ct).ConfigureAwait(false);
                _streamReadOffset = _stream.Position;
                if (_bufferLen == 0)
                {
                    if (longLineBytes is null || longLineBytes.Count == 0) return null;
                    LastLineTerminated = false;
                    return StripBom(DecodeLine(longLineBytes, ReadOnlySpan<byte>.Empty));
                }

                // Пропуск UTF-8 BOM в начале файла
                if (_streamReadOffset - _bufferLen == 0 && _bufferLen >= 3 &&
                    _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
                {
                    _bufferPos = 3;
                }
            }

            if (!recordedLineStart)
            {
                CurrentLineStartOffset = _streamReadOffset - (_bufferLen - _bufferPos);
                recordedLineStart = true;
            }

            var span = _buffer.AsSpan(_bufferPos, _bufferLen - _bufferPos);
            var newlineIdx = span.IndexOf((byte)'\n');

            if (newlineIdx >= 0)
            {
                var result = DecodeLine(longLineBytes, span.Slice(0, newlineIdx));
                _bufferPos += newlineIdx + 1;
                LastLineTerminated = true;
                return StripBom(result);
            }

            longLineBytes ??= new List<byte>(Math.Max(512, span.Length * 2));
            longLineBytes.AddRange(span);
            _bufferPos = _bufferLen;
        }
    }

    private static string DecodeLine(List<byte>? head, ReadOnlySpan<byte> tail)
    {
        if (head is null || head.Count == 0)
        {
            return Encoding.UTF8.GetString(TrimCr(tail));
        }

        head.AddRange(tail);
        return Encoding.UTF8.GetString(TrimCr(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(head)));
    }

    private static ReadOnlySpan<byte> TrimCr(ReadOnlySpan<byte> line) =>
        line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;

    private static string StripBom(string line) =>
        line.Length > 0 && line[0] == '﻿' ? line.Substring(1) : line;

    /// <summary>
    /// Сброс внутреннего буфера ридера и перепозиционирование на указанное смещение в потоке.
    /// </summary>
    public void Reset(long newOffset = 0)
    {
        _stream.Seek(newOffset, SeekOrigin.Begin);
        _bufferPos = 0;
        _bufferLen = 0;
        _streamReadOffset = newOffset;
        CurrentLineStartOffset = newOffset;
        LastLineTerminated = true;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
