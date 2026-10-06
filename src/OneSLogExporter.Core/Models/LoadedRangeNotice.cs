namespace OneSLogExporter.Core.Models;

/// <summary>
/// Подсказка под отборами таблицы: за какие дни журнал прочитан и когда. Отбор по датам работает только
/// по прочитанным записям, поэтому при выходе за эти дни подсказка становится предупреждением.
/// </summary>
public static class LoadedRangeNotice
{
    private const string DateFormat = "dd.MM.yyyy";

    public static (string Text, bool IsWarning) Build(
        DateTime? loadedMin,
        DateTime? loadedMax,
        DateTime? loadedAt,
        DateTime? filterFrom,
        DateTime? filterTo,
        string parseButton)
    {
        if (loadedAt is not { } at) return (string.Empty, false);

        var hasFilter = filterFrom.HasValue || filterTo.HasValue;
        var readAt = $"прочитано в {at:HH:mm}";

        if (loadedMin is not { } min || loadedMax is not { } max)
        {
            return hasFilter
                ? ($"Журнал {readAt}, записей нет. Чтобы прочитать выбранные даты, нажмите «{parseButton}».", true)
                : ($"Журнал {readAt}, записей нет. Новые записи — «{parseButton}».", false);
        }

        var minDay = min.Date;
        var maxDay = max.Date;
        var days = minDay == maxDay
            ? minDay.ToString(DateFormat)
            : $"{minDay.ToString(DateFormat)} — {maxDay.ToString(DateFormat)}";

        var outside = filterFrom?.Date < minDay || filterTo?.Date > maxDay;

        return outside
            ? ($"В таблице только записи за {days} ({readAt}). Чтобы прочитать выбранные даты, нажмите «{parseButton}».", true)
            : ($"В таблице записи за {days}, {readAt}. Новые записи и другие даты — «{parseButton}».", false);
    }
}
