using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using OneSLogExporter.Core.Parsers;

namespace OneSLogExporter.Core.State;

/// <summary>
/// Ограниченный по размеру набор id недавно отправленных записей (FIFO-вытеснение) с сохранением на диск.
/// Позволяет пропускать повторные записи ещё до полного разбора. Не потокобезопасен: у каждого воркера свой экземпляр.
/// </summary>
public sealed class RecentIdFilter
{
    private static ReadOnlySpan<byte> Magic => "OSLXDD01"u8;

    private readonly string _filePath;
    private readonly int _capacity;
    private readonly ILogger? _logger;
    private readonly HashSet<ulong> _set;
    private readonly Queue<ulong> _order;
    private bool _dirty;

    public RecentIdFilter(string filePath, int capacity, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _filePath = filePath;
        _capacity = capacity;
        _logger = logger;
        int initial = Math.Min(capacity, 65_536);
        _set = new HashSet<ulong>(initial);
        _order = new Queue<ulong>(initial);
    }

    public int Count => _set.Count;

    public bool Contains(ReadOnlySpan<char> id) => _set.Contains(LogRecordId.ToKey(id));

    /// <summary>Добавляет id; возвращает false, если он уже был в наборе.</summary>
    public bool Add(ReadOnlySpan<char> id) => AddKey(LogRecordId.ToKey(id));

    private bool AddKey(ulong key)
    {
        if (!_set.Add(key)) return false;

        _order.Enqueue(key);
        while (_order.Count > _capacity)
        {
            _set.Remove(_order.Dequeue());
        }
        _dirty = true;
        return true;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            byte[] data = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            if (data.Length < Magic.Length + sizeof(int) || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                _logger?.LogWarning("Файл фильтра дублей {Path} повреждён или имеет неизвестный формат, начинаем с пустого набора.", _filePath);
                return;
            }

            int offset = Magic.Length;
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            offset += sizeof(int);
            if (count < 0 || (long)count * sizeof(ulong) != data.Length - offset)
            {
                _logger?.LogWarning("Файл фильтра дублей {Path} обрезан, начинаем с пустого набора.", _filePath);
                return;
            }

            _set.Clear();
            _order.Clear();
            // В файле id лежат от старых к новым; при меньшей ёмкости остаются самые свежие.
            int skip = Math.Max(0, count - _capacity);
            for (int i = skip; i < count; i++)
            {
                AddKey(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + i * sizeof(ulong))));
            }
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Не удалось прочитать файл фильтра дублей {Path}, начинаем с пустого набора.", _filePath);
        }
    }

    /// <summary>Атомарно сохраняет набор (temp-файл + замена), только если были изменения.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!_dirty) return;

        byte[] data = new byte[Magic.Length + sizeof(int) + _order.Count * sizeof(ulong)];
        Magic.CopyTo(data);
        int offset = Magic.Length;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), _order.Count);
        offset += sizeof(int);
        foreach (ulong key in _order)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), key);
            offset += sizeof(ulong);
        }

        string? dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tempPath = _filePath + ".tmp";
        await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await fs.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            // Данные на диске до переименования: иначе при сбое питания можно получить пустой файл.
            fs.Flush(flushToDisk: true);
        }
        File.Move(tempPath, _filePath, overwrite: true);
        _dirty = false;
    }
}
