using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace OneSLogExporter.Core.Parsers;

/// <summary>
/// Детерминированный идентификатор записи лога: SHA-256 от области (база / процесс+час) и сырого текста записи.
/// Одинаковая запись всегда получает одинаковый id — в любом процессе и после перезапуска службы.
/// </summary>
public static class LogRecordId
{
    private const int StackLimit = 1024;

    /// <summary>
    /// Считает id (32 hex-символа) по области и сырому блоку записи.
    /// Пробелы по краям и символы '\r' игнорируются, поэтому CRLF и LF дают один id.
    /// </summary>
    public static string Compute(string? scope, ReadOnlySpan<char> rawBlock)
    {
        var block = rawBlock.Trim();
        var scopeSpan = scope.AsSpan();
        // Маркер наличия области отличает null от пустой строки и от совпадающего префикса блока.
        int maxBytes = Encoding.UTF8.GetMaxByteCount(scopeSpan.Length + block.Length + 2);

        byte[]? rented = null;
        Span<byte> buffer = maxBytes <= StackLimit
            ? stackalloc byte[StackLimit]
            : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            int len = 0;
            buffer[len++] = scope is null ? (byte)0 : (byte)1;
            len += Encoding.UTF8.GetBytes(scopeSpan, buffer[len..]);
            buffer[len++] = (byte)'\n';
            len += Encoding.UTF8.GetBytes(block, buffer[len..]);

            // Убираем '\r' на месте: id не должен зависеть от стиля переводов строк.
            int write = 0;
            for (int read = 0; read < len; read++)
            {
                byte b = buffer[read];
                if (b != (byte)'\r') buffer[write++] = b;
            }

            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(buffer[..write], hash);
            return Convert.ToHexStringLower(hash[..16]);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Компактный 64-битный ключ id (FNV-1a) для фильтра недавно отправленных записей.
    /// </summary>
    public static ulong ToKey(ReadOnlySpan<char> id)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in id)
        {
            hash ^= (byte)c;
            hash *= 1099511628211UL;
            hash ^= (byte)(c >> 8);
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
