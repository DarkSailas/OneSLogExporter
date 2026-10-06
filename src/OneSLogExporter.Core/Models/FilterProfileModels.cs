namespace OneSLogExporter.Core.Models;

/// <summary>
/// Корневой объект переносимого профиля фильтрации и настроек приложения 1С: Log Viewer &amp; Exporter.
/// Позволяет инженерам и аналитикам делиться готовыми конфигурациями отборов.
/// </summary>
public sealed class FilterProfile
{
    /// <summary>
    /// Версия схемы профиля (например "1.2.0").
    /// </summary>
    public string Version { get; set; } = "1.2.0";

    /// <summary>
    /// Название профиля (например "Анализ блокировок СУБД", "Ошибки регламентных заданий").
    /// </summary>
    public string Title { get; set; } = "Профиль фильтрации 1С";

    /// <summary>
    /// Пользовательское описание или примечания к профилю.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Точное время и дата экспорта профиля.
    /// </summary>
    public DateTime ExportedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Настройки фильтрации Технологического Журнала (ТЖ).
    /// </summary>
    public TechLogFilterProfile? TechLog { get; set; }

    /// <summary>
    /// Настройки фильтрации Журнала Регистрации (ЖР).
    /// </summary>
    public EventLogFilterProfile? EventLog { get; set; }

    /// <summary>
    /// Системные настройки приложения (пути к каталогам логов, Elastic, Kibana, FileDump).
    /// </summary>
    public AppSettingsProfile? Settings { get; set; }
}

/// <summary>
/// Снимок настроек фильтрации данных Технологического Журнала (ТЖ).
/// </summary>
public sealed class TechLogFilterProfile
{
    public string? LogPath { get; set; }
    public string SearchText { get; set; } = "";
    public string TimeFrom { get; set; } = "";
    public string TimeTo { get; set; } = "";
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int EventFilterIndex { get; set; }
    public bool IncludeRunning { get; set; }
    public bool IncludeCompleted { get; set; }
    public int MinDurationIndex { get; set; }
    public int SortPresetIndex { get; set; }
    public int LimitIndex { get; set; } = 0; // "Все записи"

    public bool ExcludeRphost { get; set; }
    public bool ExcludeRmngr { get; set; }
    public bool ExcludeRagent { get; set; }
    public bool ExcludeCompleted { get; set; }
    public bool ExcludeRunning { get; set; }
    public List<string> ExcludeEvents { get; set; } = [];

    public List<string> IncludedUsers { get; set; } = [];
    public List<string> ExcludedUsers { get; set; } = [];
    public List<string> IncludedApps { get; set; } = [];
    public List<string> ExcludedApps { get; set; } = [];
    public List<string> IncludedPids { get; set; } = [];
    public List<string> ExcludedPids { get; set; } = [];
    public List<string> IncludedSpids { get; set; } = [];
    public List<string> ExcludedSpids { get; set; } = [];
    public List<string> IncludedThreads { get; set; } = [];
    public List<string> ExcludedThreads { get; set; } = [];
}

/// <summary>
/// Снимок настроек фильтрации данных Журнала Регистрации (ЖР).
/// </summary>
public sealed class EventLogFilterProfile
{
    public string? LogPath { get; set; }
    public string SearchText { get; set; } = "";
    public string TimeFrom { get; set; } = "";
    public string TimeTo { get; set; } = "";
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int ImportanceIndex { get; set; }
    public bool IncludeError { get; set; }
    public bool IncludeWarn { get; set; }
    public bool IncludeInfo { get; set; }
    public bool IncludeNote { get; set; }
    public int SortPresetIndex { get; set; }
    public int LimitIndex { get; set; } = 0; // "Все записи"

    public bool ExcludeError { get; set; }
    public bool ExcludeWarn { get; set; }
    public bool ExcludeInfo { get; set; }
    public bool ExcludeNote { get; set; }
    public List<string> ExcludeEvents { get; set; } = [];

    public List<string> IncludedUsers { get; set; } = [];
    public List<string> ExcludedUsers { get; set; } = [];
    public List<string> IncludedApps { get; set; } = [];
    public List<string> ExcludedApps { get; set; } = [];
    public List<string> IncludedMetas { get; set; } = [];
    public List<string> ExcludedMetas { get; set; } = [];
    public List<string> IncludedEvents { get; set; } = [];
    public List<string> ExcludedEvents { get; set; } = [];
}

/// <summary>
/// Снимок системных настроек подключений и путей.
/// </summary>
public sealed class AppSettingsProfile
{
    public string? TechLogPath { get; set; }
    public string? EventLogPath { get; set; }
    public string? ElasticUrl { get; set; }
    public string? ElasticUser { get; set; }
    public string? ElasticApiKey { get; set; }
    public bool ElasticEnabled { get; set; } = true;
    public string? EventLogIndexPrefix { get; set; }
    public string? TechLogIndexPrefix { get; set; }
    public string? KibanaUrl { get; set; }
    public string? DumpDirectory { get; set; }
    public string? DumpDirectoryTechLog { get; set; }
    public string? DumpDirectoryEventLog { get; set; }
    public int? DumpMaxTotalSizeMb { get; set; }
    public string? ClickHouseUrl { get; set; }
    public string? ClickHouseDb { get; set; }
    public string? ClickHouseUser { get; set; }
    public bool ClickHouseEnabled { get; set; } = false;
    public string? ClickHouseTechLogTable { get; set; }
    public string? ClickHouseEventLogTable { get; set; }
    public int ClickHouseBatchSize { get; set; } = 5000;
}
