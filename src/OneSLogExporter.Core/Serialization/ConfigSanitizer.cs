using System.Text;
using System.Text.RegularExpressions;

namespace OneSLogExporter.Core.Serialization;

/// <summary>
/// Автоматический нормализатор путей и обратных слешей (\) в конфигурационном файле JSON.
/// Позволяет пользователям указывать стандартные пути Windows (C:\Logs\...) без ручного экранирования.
/// </summary>
public static class ConfigSanitizer
{
    private static readonly Regex DrivePathRegex = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    /// <summary>
    /// Проверяет и при необходимости автоматически исправляет неэкранированные обратные слеши в appsettings.json.
    /// </summary>
    public static bool SanitizeConfigFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            var content = File.ReadAllText(filePath, Encoding.UTF8);
            var sanitized = SanitizeJson(content);
            if (!string.Equals(content, sanitized, StringComparison.Ordinal))
            {
                File.WriteAllText(filePath, sanitized, Encoding.UTF8);
                return true;
            }
        }
        catch
        {
            // Безопасное подавление исключений, чтобы не препятствовать обычному циклу загрузки
        }

        return false;
    }

    /// <summary>
    /// Нормализует обратные слеши в строковых значениях JSON (особенно путях к файлам и каталогам).
    /// </summary>
    public static string SanitizeJson(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        var lines = json.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var modified = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Пропускаем строки без двоеточия или без кавычек
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;

            var firstQuote = line.IndexOf('"', colonIdx + 1);
            if (firstQuote < 0) continue;

            var lastQuote = line.LastIndexOf('"');
            if (lastQuote <= firstQuote) continue;

            // Если есть однострочный комментарий // после значения, ищем закрывающую кавычку до комментария
            var commentIdx = line.IndexOf("//", colonIdx + 1, StringComparison.Ordinal);
            if (commentIdx > 0 && lastQuote > commentIdx)
            {
                lastQuote = line.LastIndexOf('"', commentIdx);
                if (lastQuote <= firstQuote) continue;
            }

            var valueSpan = line.AsSpan(firstQuote + 1, lastQuote - firstQuote - 1);
            if (!valueSpan.Contains('\\')) continue;

            var valStr = valueSpan.ToString();
            var propertyNameSpan = line.AsSpan(0, colonIdx);

            var isPathSetting = propertyNameSpan.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                                propertyNameSpan.Contains("Directory", StringComparison.OrdinalIgnoreCase) ||
                                propertyNameSpan.Contains("File", StringComparison.OrdinalIgnoreCase) ||
                                DrivePathRegex.IsMatch(valStr);

            if (isPathSetting)
            {
                // В путях Windows заменяем и одиночные '\', и двойные '\\' на прямые слеши '/'
                var newVal = valStr.Replace(@"\\", "/").Replace('\\', '/');
                if (!string.Equals(newVal, valStr, StringComparison.Ordinal))
                {
                    lines[i] = string.Concat(line.AsSpan(0, firstQuote + 1), newVal, line.AsSpan(lastQuote));
                    modified = true;
                }
            }
            else
            {
                var fixedVal = FixInvalidEscapes(valStr);
                if (!string.Equals(fixedVal, valStr, StringComparison.Ordinal))
                {
                    lines[i] = string.Concat(line.AsSpan(0, firstQuote + 1), fixedVal, line.AsSpan(lastQuote));
                    modified = true;
                }
            }
        }

        var separator = json.Contains("\r\n") ? "\r\n" : "\n";
        return modified ? string.Join(separator, lines) : json;
    }

    private static string FixInvalidEscapes(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\')
            {
                if (i + 1 >= value.Length)
                {
                    sb.Append('/');
                    continue;
                }

                var next = value[i + 1];
                // Допустимые escape-символы RFC 8259: ", \, /, b, f, n, r, t, u
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
                {
                    sb.Append(c);
                }
                else
                {
                    // Недопустимая последовательность (например \m, \1) — заменяем на /
                    sb.Append('/');
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
