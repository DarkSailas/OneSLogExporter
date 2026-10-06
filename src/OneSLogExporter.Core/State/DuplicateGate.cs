using Microsoft.Extensions.Logging;

namespace OneSLogExporter.Core.State;

/// <summary>
/// Решает, пропускать ли запись как дубль: id уже отправлялся (RecentIdFilter) или уже есть в текущей неотправленной пачке.
/// Id попадает в фильтр только после успешной отправки пачки (Commit), поэтому сбой отправки не приводит к потере данных.
/// Не потокобезопасен: у каждого воркера свой экземпляр.
/// </summary>
public sealed class DuplicateGate
{
    private readonly RecentIdFilter? _filter;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    private DuplicateGate(RecentIdFilter? filter)
    {
        _filter = filter;
    }

    /// <summary>Отключённый шлюз: ничего не пропускает и ничего не хранит.</summary>
    public static DuplicateGate Disabled { get; } = new(null);

    public static DuplicateGate Create(bool enabled, string filePath, int capacity, ILogger? logger = null) =>
        enabled ? new DuplicateGate(new RecentIdFilter(filePath, Math.Max(1, capacity), logger)) : Disabled;

    public bool Enabled => _filter is not null;

    /// <summary>Число записей, пропущенных как дубли с момента последнего TakeSkippedCount.</summary>
    public long SkippedCount { get; private set; }

    /// <summary>Предикат для парсеров (null, если дедупликация выключена).</summary>
    public Func<string, bool>? Predicate => _filter is null ? null : IsDuplicate;

    public bool IsDuplicate(string id)
    {
        if (_filter is null) return false;

        if (_filter.Contains(id) || !_pending.Add(id))
        {
            SkippedCount++;
            return true;
        }
        return false;
    }

    /// <summary>Фиксирует id успешно отправленной пачки.</summary>
    public void Commit(IEnumerable<string> ids)
    {
        if (_filter is null) return;

        foreach (var id in ids)
        {
            _filter.Add(id);
        }
        _pending.Clear();
    }

    /// <summary>Сбрасывает id неотправленной пачки (перед разбором очередного файла или после ошибки).</summary>
    public void ResetPending() => _pending.Clear();

    public long TakeSkippedCount()
    {
        var count = SkippedCount;
        SkippedCount = 0;
        return count;
    }

    public Task LoadAsync(CancellationToken ct) => _filter?.LoadAsync(ct) ?? Task.CompletedTask;

    public Task SaveAsync(CancellationToken ct) => _filter?.SaveAsync(ct) ?? Task.CompletedTask;
}
