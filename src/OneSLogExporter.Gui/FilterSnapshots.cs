using OneSLogExporter.Core.Models;

namespace OneSLogExporter.Gui;

/// <summary>
/// Неизменяемый снимок всех активных параметров отбора Технологического Журнала (ТЖ)
/// для потоковой сквозной фильтрации в фоновых воркерах и сверхбыстрой фильтрации в UI.
/// </summary>
public sealed class TgFilterSnapshot
{
    public string? Search { get; }
    public (string Token, bool IsNegative)[] SearchTokens { get; }
    public int EventIndex { get; }
    public string? EventTag { get; }
    public string? EventText { get; }
    public HashSet<string>? ExcludedEvents { get; }
    public bool IncRunning { get; }
    public bool IncCompleted { get; }
    public bool ExRunning { get; }
    public bool ExCompleted { get; }
    public bool ExRphost { get; }
    public bool ExRmngr { get; }
    public bool ExRagent { get; }
    public int MinDurationMs { get; }
    public DateTime? DateFrom { get; }
    public DateTime? DateTo { get; }
    public bool HasTimeFrom { get; }
    public TimeSpan TimeFrom { get; }
    public DateTime ExactFrom { get; }
    public bool HasTimeTo { get; }
    public TimeSpan TimeTo { get; }
    public DateTime ExactTo { get; }
    public HashSet<string>? UsersInc { get; }
    public HashSet<string>? UsersEx { get; }
    public HashSet<string>? AppsInc { get; }
    public HashSet<string>? AppsEx { get; }
    public HashSet<string>? PidsInc { get; }
    public HashSet<string>? PidsEx { get; }
    public HashSet<string>? SpidsInc { get; }
    public HashSet<string>? SpidsEx { get; }
    public HashSet<string>? ThreadsInc { get; }
    public HashSet<string>? ThreadsEx { get; }

    public bool HasAnyCriteria { get; }

    public TgFilterSnapshot()
    {
        SearchTokens = [];
        HasAnyCriteria = false;
    }

    public TgFilterSnapshot(
        string? search,
        (string Token, bool IsNegative)[] searchTokens,
        int eventIndex,
        string? eventTag,
        string? eventText,
        HashSet<string>? excludedEvents,
        bool incRunning, bool incCompleted,
        bool exRunning, bool exCompleted,
        bool exRphost, bool exRmngr, bool exRagent,
        int minDurationMs,
        DateTime? dateFrom, DateTime? dateTo,
        bool hasTimeFrom, TimeSpan timeFrom, DateTime exactFrom,
        bool hasTimeTo, TimeSpan timeTo, DateTime exactTo,
        HashSet<string>? usersInc, HashSet<string>? usersEx,
        HashSet<string>? appsInc, HashSet<string>? appsEx,
        HashSet<string>? pidsInc, HashSet<string>? pidsEx,
        HashSet<string>? spidsInc, HashSet<string>? spidsEx,
        HashSet<string>? threadsInc, HashSet<string>? threadsEx)
    {
        Search = search;
        SearchTokens = searchTokens ?? [];
        EventIndex = eventIndex;
        EventTag = eventTag;
        EventText = eventText;
        ExcludedEvents = excludedEvents;
        IncRunning = incRunning;
        IncCompleted = incCompleted;
        ExRunning = exRunning;
        ExCompleted = exCompleted;
        ExRphost = exRphost;
        ExRmngr = exRmngr;
        ExRagent = exRagent;
        MinDurationMs = minDurationMs;
        DateFrom = dateFrom;
        DateTo = dateTo;
        HasTimeFrom = hasTimeFrom;
        TimeFrom = timeFrom;
        ExactFrom = exactFrom;
        HasTimeTo = hasTimeTo;
        TimeTo = timeTo;
        ExactTo = exactTo;
        UsersInc = usersInc;
        UsersEx = usersEx;
        AppsInc = appsInc;
        AppsEx = appsEx;
        PidsInc = pidsInc;
        PidsEx = pidsEx;
        SpidsInc = spidsInc;
        SpidsEx = spidsEx;
        ThreadsInc = threadsInc;
        ThreadsEx = threadsEx;

        HasAnyCriteria = SearchTokens.Length > 0
            || EventIndex > 0
            || (ExcludedEvents != null && ExcludedEvents.Count > 0)
            || IncRunning || IncCompleted || ExRunning || ExCompleted
            || ExRphost || ExRmngr || ExRagent
            || MinDurationMs > 0
            || DateFrom.HasValue || DateTo.HasValue
            || HasTimeFrom || HasTimeTo
            || UsersInc != null || UsersEx != null
            || AppsInc != null || AppsEx != null
            || PidsInc != null || PidsEx != null
            || SpidsInc != null || SpidsEx != null
            || ThreadsInc != null || ThreadsEx != null;
    }

    public bool Matches(TechLogDoc doc)
    {
        // 1. Поисковый запрос с поддержкой отрицаний '!' и '-'
        if (SearchTokens.Length > 0)
        {
            foreach (var (token, isNegative) in SearchTokens)
            {
                bool found = MainWindow.ContainsTgToken(doc, token);
                if (isNegative ? found : !found) return false;
            }
        }

        // 2. Включающий фильтр событий из ComboBox
        if (EventIndex > 0)
        {
            var docEv = doc.Event ?? string.Empty;
            bool matchesEvent;

            if (!string.IsNullOrEmpty(EventTag))
            {
                matchesEvent = docEv.Equals(EventTag, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var text = EventText ?? string.Empty;
                if (text.Contains("LONGDURATIONINFO", StringComparison.OrdinalIgnoreCase))
                    matchesEvent = docEv.Equals("LONGDURATIONINFO", StringComparison.OrdinalIgnoreCase);
                else if (text.Contains("EXCP", StringComparison.OrdinalIgnoreCase))
                    matchesEvent = docEv.StartsWith("EXCP", StringComparison.OrdinalIgnoreCase) || docEv.Equals("QERR", StringComparison.OrdinalIgnoreCase);
                else if (text.Contains("TLOCK", StringComparison.OrdinalIgnoreCase))
                    matchesEvent = docEv.Contains("TLOCK", StringComparison.OrdinalIgnoreCase) || docEv.Contains("TDEADLOCK", StringComparison.OrdinalIgnoreCase) || docEv.Contains("TTIMEOUT", StringComparison.OrdinalIgnoreCase);
                else if (text.Contains("DBMSSQL", StringComparison.OrdinalIgnoreCase))
                    matchesEvent = docEv.Contains("DBMSSQL", StringComparison.OrdinalIgnoreCase) || docEv.Contains("SDBL", StringComparison.OrdinalIgnoreCase) || docEv.Contains("DBPOSTGRS", StringComparison.OrdinalIgnoreCase) || docEv.Contains("DBORACLE", StringComparison.OrdinalIgnoreCase) || docEv.Contains("DBV8DBENG", StringComparison.OrdinalIgnoreCase);
                else if (text.Contains("CALL", StringComparison.OrdinalIgnoreCase))
                    matchesEvent = docEv.Contains("CALL", StringComparison.OrdinalIgnoreCase) || docEv.Contains("SCALL", StringComparison.OrdinalIgnoreCase) || docEv.Contains("RUNMETH", StringComparison.OrdinalIgnoreCase);
                else
                    matchesEvent = true;
            }

            if (!matchesEvent) return false;
        }

        // 3. Исключающие события ТЖ
        var docEvent = doc.Event ?? string.Empty;
        if (ExcludedEvents != null && ExcludedEvents.Contains(docEvent))
            return false;

        // 4. Включающий фильтр по статусу операции
        if (IncRunning && !IncCompleted)
        {
            if (!doc.IsActiveOperation) return false;
        }
        else if (!IncRunning && IncCompleted)
        {
            if (doc.IsActiveOperation) return false;
        }

        // 5. Исключающий фильтр по статусу операции
        if (ExCompleted && !doc.IsActiveOperation) return false;
        if (ExRunning && doc.IsActiveOperation) return false;

        // 6. Исключающий фильтр по процессам
        var pName = doc.ProcessName ?? string.Empty;
        if (ExRphost && pName.Contains("rphost", StringComparison.OrdinalIgnoreCase)) return false;
        if (ExRmngr && pName.Contains("rmngr", StringComparison.OrdinalIgnoreCase)) return false;
        if (ExRagent && pName.Contains("ragent", StringComparison.OrdinalIgnoreCase)) return false;

        // 7. Порог длительности (мс)
        if (MinDurationMs > 0 && doc.DurationMs < MinDurationMs) return false;

        // 7.5. Фильтр по дате календаря
        if (DateFrom.HasValue || DateTo.HasValue)
        {
            var docDate = doc.Date.Date;
            if (DateFrom.HasValue && docDate < DateFrom.Value) return false;
            if (DateTo.HasValue && docDate > DateTo.Value) return false;
        }

        // 8. Фильтр по времени события
        if (HasTimeFrom)
        {
            if (ExactFrom != default)
            {
                if (doc.Date < ExactFrom) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay < TimeFrom) return false;
            }
        }
        if (HasTimeTo)
        {
            if (ExactTo != default)
            {
                if (doc.Date > ExactTo) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay > TimeTo) return false;
            }
        }

        // 9. Динамический отбор по заполненным полям ТЖ
        var user = doc.User ?? string.Empty;
        if (UsersEx != null && UsersEx.Contains(user)) return false;
        if (UsersInc != null && !UsersInc.Contains(user)) return false;

        var app = doc.App ?? string.Empty;
        if (AppsEx != null && AppsEx.Contains(app)) return false;
        if (AppsInc != null && !AppsInc.Contains(app)) return false;

        var pid = doc.ProcessId ?? string.Empty;
        if (PidsEx != null && PidsEx.Contains(pid)) return false;
        if (PidsInc != null && !PidsInc.Contains(pid)) return false;

        var spid = doc.Spid ?? string.Empty;
        if (SpidsEx != null && SpidsEx.Contains(spid)) return false;
        if (SpidsInc != null && !SpidsInc.Contains(spid)) return false;

        var thread = doc.OSThread ?? string.Empty;
        if (ThreadsEx != null && ThreadsEx.Contains(thread)) return false;
        if (ThreadsInc != null && !ThreadsInc.Contains(thread)) return false;

        return true;
    }
}

/// <summary>
/// Неизменяемый снимок всех активных параметров отбора Журнала Регистрации (ЖР)
/// для потоковой сквозной фильтрации в фоновых воркерах и сверхбыстрой фильтрации в UI.
/// </summary>
public sealed class LgFilterSnapshot
{
    public string? Search { get; }
    public (string Token, bool IsNegative)[] SearchTokens { get; }
    public int ImportanceIndex { get; }
    public string? ImportanceTag { get; }
    public string? ImportanceText { get; }
    public bool IncError { get; }
    public bool IncWarn { get; }
    public bool IncInfo { get; }
    public bool IncNote { get; }
    public bool ExError { get; }
    public bool ExWarn { get; }
    public bool ExInfo { get; }
    public bool ExNote { get; }
    public HashSet<string>? ExcludedEvents { get; }
    public DateTime? DateFrom { get; }
    public DateTime? DateTo { get; }
    public bool HasTimeFrom { get; }
    public TimeSpan TimeFrom { get; }
    public DateTime ExactFrom { get; }
    public bool HasTimeTo { get; }
    public TimeSpan TimeTo { get; }
    public DateTime ExactTo { get; }
    public HashSet<string>? UsersInc { get; }
    public HashSet<string>? UsersEx { get; }
    public HashSet<string>? AppsInc { get; }
    public HashSet<string>? AppsEx { get; }
    public HashSet<string>? MetasInc { get; }
    public HashSet<string>? MetasEx { get; }
    public HashSet<string>? EventsInc { get; }
    public HashSet<string>? EventsEx { get; }

    public bool HasIncLevel => IncError || IncWarn || IncInfo || IncNote;
    public bool HasExLevel => ExError || ExWarn || ExInfo || ExNote;
    public bool HasAnyCriteria { get; }

    public LgFilterSnapshot()
    {
        SearchTokens = [];
        HasAnyCriteria = false;
    }

    public LgFilterSnapshot(
        string? search,
        (string Token, bool IsNegative)[] searchTokens,
        int importanceIndex,
        string? importanceTag,
        string? importanceText,
        bool incError, bool incWarn, bool incInfo, bool incNote,
        bool exError, bool exWarn, bool exInfo, bool exNote,
        HashSet<string>? excludedEvents,
        DateTime? dateFrom, DateTime? dateTo,
        bool hasTimeFrom, TimeSpan timeFrom, DateTime exactFrom,
        bool hasTimeTo, TimeSpan timeTo, DateTime exactTo,
        HashSet<string>? usersInc, HashSet<string>? usersEx,
        HashSet<string>? appsInc, HashSet<string>? appsEx,
        HashSet<string>? metasInc, HashSet<string>? metasEx,
        HashSet<string>? eventsInc, HashSet<string>? eventsEx)
    {
        Search = search;
        SearchTokens = searchTokens ?? [];
        ImportanceIndex = importanceIndex;
        ImportanceTag = importanceTag;
        ImportanceText = importanceText;
        IncError = incError;
        IncWarn = incWarn;
        IncInfo = incInfo;
        IncNote = incNote;
        ExError = exError;
        ExWarn = exWarn;
        ExInfo = exInfo;
        ExNote = exNote;
        ExcludedEvents = excludedEvents;
        DateFrom = dateFrom;
        DateTo = dateTo;
        HasTimeFrom = hasTimeFrom;
        TimeFrom = timeFrom;
        ExactFrom = exactFrom;
        HasTimeTo = hasTimeTo;
        TimeTo = timeTo;
        ExactTo = exactTo;
        UsersInc = usersInc;
        UsersEx = usersEx;
        AppsInc = appsInc;
        AppsEx = appsEx;
        MetasInc = metasInc;
        MetasEx = metasEx;
        EventsInc = eventsInc;
        EventsEx = eventsEx;

        HasAnyCriteria = SearchTokens.Length > 0
            || ImportanceIndex > 0
            || HasIncLevel || HasExLevel
            || (ExcludedEvents != null && ExcludedEvents.Count > 0)
            || DateFrom.HasValue || DateTo.HasValue
            || HasTimeFrom || HasTimeTo
            || UsersInc != null || UsersEx != null
            || AppsInc != null || AppsEx != null
            || MetasInc != null || MetasEx != null
            || EventsInc != null || EventsEx != null;
    }

    public bool Matches(EventLogDoc doc)
    {
        // 1. Поисковый запрос с поддержкой отрицаний '!' и '-'
        if (SearchTokens.Length > 0)
        {
            foreach (var (token, isNegative) in SearchTokens)
            {
                bool found = MainWindow.ContainsLgToken(doc, token);
                if (isNegative ? found : !found) return false;
            }
        }

        // 2. Включающий фильтр важности/события из ComboBox
        if (ImportanceIndex > 0)
        {
            var docImp = doc.Importance ?? string.Empty;
            var docEv = doc.Event ?? string.Empty;
            bool matches;

            if (!string.IsNullOrEmpty(ImportanceTag))
            {
                matches = docEv.Equals(ImportanceTag, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                var text = ImportanceText ?? string.Empty;
                if (text.Contains("Ошибка", StringComparison.OrdinalIgnoreCase))
                    matches = docImp.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) || docImp.Contains("Error", StringComparison.OrdinalIgnoreCase);
                else if (text.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase))
                    matches = docImp.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase) || docImp.Contains("Warn", StringComparison.OrdinalIgnoreCase);
                else if (text.Contains("Информация", StringComparison.OrdinalIgnoreCase))
                    matches = docImp.Contains("Информация", StringComparison.OrdinalIgnoreCase) || docImp.Contains("Info", StringComparison.OrdinalIgnoreCase);
                else if (text.Contains("Примечание", StringComparison.OrdinalIgnoreCase))
                    matches = docImp.Contains("Примечание", StringComparison.OrdinalIgnoreCase) || docImp.Contains("Note", StringComparison.OrdinalIgnoreCase);
                else
                    matches = true;
            }

            if (!matches) return false;
        }

        var importance = doc.Importance ?? string.Empty;
        var ev = doc.Event ?? string.Empty;

        // 3. Включающие чекбоксы уровней важности
        if (HasIncLevel)
        {
            bool matchLevel = false;
            if (IncError && (importance.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) || importance.Contains("Error", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (IncWarn && (importance.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase) || importance.Contains("Warn", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (IncInfo && (importance.Contains("Информация", StringComparison.OrdinalIgnoreCase) || importance.Contains("Info", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (IncNote && (importance.Contains("Примечание", StringComparison.OrdinalIgnoreCase) || importance.Contains("Note", StringComparison.OrdinalIgnoreCase))) matchLevel = true;
            if (!matchLevel) return false;
        }

        // 4. Исключающие чекбоксы уровней важности
        if (ExError && (importance.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) || importance.Contains("Error", StringComparison.OrdinalIgnoreCase))) return false;
        if (ExWarn && (importance.Contains("Предупреждение", StringComparison.OrdinalIgnoreCase) || importance.Contains("Warn", StringComparison.OrdinalIgnoreCase))) return false;
        if (ExInfo && (importance.Contains("Информация", StringComparison.OrdinalIgnoreCase) || importance.Contains("Info", StringComparison.OrdinalIgnoreCase))) return false;
        if (ExNote && (importance.Contains("Примечание", StringComparison.OrdinalIgnoreCase) || importance.Contains("Note", StringComparison.OrdinalIgnoreCase))) return false;

        // 5. Исключающие события ЖР
        if (ExcludedEvents != null && ExcludedEvents.Contains(ev)) return false;

        // 5.5. Фильтр по дате календаря (С / По)
        if (DateFrom.HasValue || DateTo.HasValue)
        {
            var docDate = doc.Date.Date;
            if (DateFrom.HasValue && docDate < DateFrom.Value) return false;
            if (DateTo.HasValue && docDate > DateTo.Value) return false;
        }

        // 6. Фильтр по времени события (С / По)
        if (HasTimeFrom)
        {
            if (ExactFrom != default)
            {
                if (doc.Date < ExactFrom) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay < TimeFrom) return false;
            }
        }
        if (HasTimeTo)
        {
            if (ExactTo != default)
            {
                if (doc.Date > ExactTo) return false;
            }
            else
            {
                if (doc.Date.TimeOfDay > TimeTo) return false;
            }
        }

        // 7. Динамический отбор по заполненным полям ЖР
        var user = doc.User ?? string.Empty;
        if (UsersEx != null && UsersEx.Contains(user)) return false;
        if (UsersInc != null && !UsersInc.Contains(user)) return false;

        var app = doc.App ?? string.Empty;
        if (AppsEx != null && AppsEx.Contains(app)) return false;
        if (AppsInc != null && !AppsInc.Contains(app)) return false;

        var meta = doc.Meta ?? string.Empty;
        if (MetasEx != null && MetasEx.Contains(meta)) return false;
        if (MetasInc != null && !MetasInc.Contains(meta)) return false;

        if (EventsEx != null && EventsEx.Contains(ev)) return false;
        if (EventsInc != null && !EventsInc.Contains(ev)) return false;

        return true;
    }
}
