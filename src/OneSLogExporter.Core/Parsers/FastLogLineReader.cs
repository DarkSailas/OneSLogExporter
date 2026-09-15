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
    /// Чтение следующей строки UTF-8 с фиксацией точного байтового смещения её начала.
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct = default)
    {
        if (_disposed) return null;

        StringBuilder? longLineBuilder = null;
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
                    return longLineBuilder?.ToString();
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
                var lineBytes = newlineIdx;
                if (lineBytes > 0 && span[lineBytes - 1] == (byte)'\r')
                {
                    lineBytes--;
                }

                string result;
                if (longLineBuilder != null)
                {
                    longLineBuilder.Append(Encoding.UTF8.GetString(span.Slice(0, lineBytes)));
                    result = longLineBuilder.ToString();
                }
                else
                {
                    result = Encoding.UTF8.GetString(span.Slice(0, lineBytes));
                }

                _bufferPos += (newlineIdx + 1);
                return result;
            }
            else
            {
                longLineBuilder ??= new StringBuilder(512);
                longLineBuilder.Append(Encoding.UTF8.GetString(span));
                _bufferPos = _bufferLen;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
