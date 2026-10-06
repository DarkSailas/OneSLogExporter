using System.Buffers;

namespace OneSLogExporter.Core.Models;

/// <summary>
/// Однострочное представление значения поля для ячейки таблицы: переводы строк и табуляции заменяются пробелом,
/// длинный текст обрезается. Полное значение остаётся в записи и показывается в окне деталей.
/// </summary>
public static class SingleLineText
{
    /// <summary>Больше символов в ячейку таблицы всё равно не помещается.</summary>
    public const int DefaultMaxLength = 400;

    private const char Ellipsis = '…';

    private static readonly SearchValues<char> LineBreakChars = SearchValues.Create(['\r', '\n', '\t']);

    public static string From(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxLength && !text.AsSpan().ContainsAny(LineBreakChars)) return text;

        var buffer = ArrayPool<char>.Shared.Rent(maxLength + 1);
        try
        {
            var length = 0;
            var pendingSpace = false;
            var isCut = false;

            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch is '\r' or '\n' or '\t')
                {
                    // Пробел ставится только перед следующим видимым символом — в начале и в конце он не нужен.
                    pendingSpace = length > 0;
                    continue;
                }

                var isPair = char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
                var needed = (pendingSpace ? 1 : 0) + (isPair ? 2 : 1);
                if (length + needed > maxLength)
                {
                    isCut = true;
                    break;
                }

                if (pendingSpace)
                {
                    buffer[length++] = ' ';
                    pendingSpace = false;
                }

                buffer[length++] = ch;
                if (isPair)
                {
                    buffer[length++] = text[++i];
                }
            }

            if (isCut)
            {
                buffer[length++] = Ellipsis;
            }

            return new string(buffer, 0, length);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
