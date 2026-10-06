using System.Globalization;

namespace OneSLogExporter.Core.Models;

/// <summary>
/// Разбор поля отбора по времени. Время суток принимается в любом удобном виде:
/// «16:13», «16.13», «1613», «4», «16:13:50», «16:13:50.875». Дата со временем — «2026-08-17 16:13», «17.08.2026 16:13».
/// </summary>
public static class TimeFilterInput
{
    /// <summary>
    /// Для времени суток <paramref name="exactDate"/> остаётся пустой, для даты со временем заполняется.
    /// </summary>
    public static bool TryParse(string? input, out TimeSpan timeOfDay, out DateTime exactDate, CultureInfo? culture = null)
    {
        timeOfDay = default;
        exactDate = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        // Время суток проверяется первым: иначе «04.05» читается как 4 мая, а «16:13» — как сегодняшняя дата.
        if (TryParseTimeOfDay(input, out timeOfDay)) return true;

        if (DateTime.TryParse(input.AsSpan().Trim(), culture ?? CultureInfo.CurrentCulture, DateTimeStyles.None, out exactDate))
        {
            timeOfDay = exactDate.TimeOfDay;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Приводит время суток к виду «ЧЧ:мм» (секунды и миллисекунды добавляются, если они заданы).
    /// Дата со временем и нераспознанный текст возвращаются без изменений.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var trimmed = input.Trim();
        if (!TryParseTimeOfDay(trimmed, out var time)) return trimmed;

        if (time.Milliseconds != 0) return time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        if (time.Seconds != 0) return time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        return time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    public static bool TryParseTimeOfDay(string? input, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        // Незаконченный ввод «16:» считается как «16».
        var text = input.AsSpan().Trim().TrimEnd(":.,");
        if (text.IsEmpty) return false;

        ReadOnlySpan<char> hours, minutes = default, seconds = default, fraction = default;

        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            // «16:13», «16:13:50», «16:13:50.875»
            hours = text[..colon];
            var rest = text[(colon + 1)..];
            var secondColon = rest.IndexOf(':');
            if (secondColon < 0)
            {
                minutes = rest;
            }
            else
            {
                minutes = rest[..secondColon];
                seconds = rest[(secondColon + 1)..];
                var dot = seconds.IndexOfAny('.', ',');
                if (dot >= 0)
                {
                    fraction = seconds[(dot + 1)..];
                    seconds = seconds[..dot];
                    if (fraction.IsEmpty) return false;
                }
            }
        }
        else if (text.IndexOfAny('.', ',') >= 0)
        {
            // «16.13», «16.13.50», «16.13.50.875»
            Span<Range> parts = stackalloc Range[5];
            var count = text.SplitAny(parts, ".,");
            if (count is < 2 or > 4) return false;

            hours = text[parts[0]];
            minutes = text[parts[1]];
            if (count > 2) seconds = text[parts[2]];
            if (count > 3) fraction = text[parts[3]];
            if (count > 2 && seconds.IsEmpty) return false;
            if (count > 3 && fraction.IsEmpty) return false;
        }
        else
        {
            // Только цифры: «4», «16», «405», «1613», «161350»
            switch (text.Length)
            {
                case 1 or 2:
                    hours = text;
                    break;
                case 3 or 4:
                    hours = text[..^2];
                    minutes = text[^2..];
                    break;
                case 5 or 6:
                    hours = text[..^4];
                    minutes = text[^4..^2];
                    seconds = text[^2..];
                    break;
                default:
                    return false;
            }
        }

        if (!TryParsePart(hours, 23, required: true, out var h)) return false;
        if (!TryParsePart(minutes, 59, required: colon >= 0, out var m)) return false;
        if (!TryParsePart(seconds, 59, required: false, out var s)) return false;
        if (!TryParseFraction(fraction, out var ticks)) return false;

        timeOfDay = new TimeSpan(h, m, s) + TimeSpan.FromTicks(ticks);
        return true;
    }

    private static bool TryParsePart(ReadOnlySpan<char> part, int max, bool required, out int value)
    {
        value = 0;
        if (part.IsEmpty) return !required;
        if (part.Length > 2) return false;

        foreach (var ch in part)
        {
            if (!char.IsAsciiDigit(ch)) return false;
            value = value * 10 + (ch - '0');
        }

        return value <= max;
    }

    private static bool TryParseFraction(ReadOnlySpan<char> fraction, out long ticks)
    {
        ticks = 0;
        if (fraction.IsEmpty) return true;
        if (fraction.Length > 7) return false;

        long scale = TimeSpan.TicksPerSecond;
        foreach (var ch in fraction)
        {
            if (!char.IsAsciiDigit(ch)) return false;
            scale /= 10;
            ticks += (ch - '0') * scale;
        }

        return true;
    }
}
