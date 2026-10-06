using OneSLogExporter.Core.Models;

namespace OneSLogExporter.Core.Services;

/// <summary>
/// Схлопывает повторяющиеся события ЖР внутри пачки: одна запись на (УИД метаданных, событие, пользователь, окно времени).
/// Окна выровнены по времени (Date.Ticks / window), поэтому повторный разбор того же файла даёт тот же результат.
/// Окно, попавшее на границу двух пачек, даёт две записи; сумма RepeatCount при этом остаётся точной.
/// </summary>
public static class EventLogCollapser
{
    public static IReadOnlyList<EventLogDoc> Collapse(IReadOnlyList<EventLogDoc> batch, TimeSpan window)
    {
        if (batch.Count < 2 || window <= TimeSpan.Zero)
            return batch;

        var result = new List<EventLogDoc>(batch.Count);
        // Ключ -> индекс первой записи группы в result.
        var groups = new Dictionary<(string Meta, string Event, string User, long Bucket), int>();

        foreach (var doc in batch)
        {
            if (string.IsNullOrEmpty(doc.MetadataUuid))
            {
                result.Add(doc);
                continue;
            }

            var key = (
                doc.MetadataUuid,
                doc.Event ?? string.Empty,
                doc.UserUuid ?? doc.User ?? string.Empty,
                doc.Date.Ticks / window.Ticks);

            if (groups.TryGetValue(key, out var index))
            {
                var first = result[index];
                result[index] = first with { RepeatCount = first.RepeatCount + doc.RepeatCount };
            }
            else
            {
                groups[key] = result.Count;
                result.Add(doc);
            }
        }

        return result;
    }
}
