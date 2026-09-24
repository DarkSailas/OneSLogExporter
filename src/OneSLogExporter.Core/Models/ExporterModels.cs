using System.Text.Json.Serialization;
using OneSLogExporter.Core.Serialization;

namespace OneSLogExporter.Core.Models;

/// <summary>
/// Модель структурированного документа записи Журнала Регистрации 1С для экспорта.
/// JSON-имена свойств выровнены со схемой OneSTools (akpaevj/OneSTools.EventLog) для совместимости с ClickHouse.
/// </summary>
public sealed record EventLogDoc
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Совместимость со схемами ClickHouse, где идентификатор записи называется Id (с заглавной буквы).
    /// </summary>
    [JsonPropertyName("Id")]
    public string IdAlias => Id;

    [JsonPropertyName("DateTime")]
    [JsonConverter(typeof(ClickHouseDateTimeConverter))]
    public required DateTime Date { get; init; }

    /// <summary>
    /// Совместимость с существующими таблицами ClickHouse, где физическая колонка ключа партиционирования называется Date.
    /// </summary>
    [JsonPropertyName("Date")]
    [JsonConverter(typeof(ClickHouseDateTimeConverter))]
    public DateTime DateLegacy => Date;

    public required string DateFormatted { get; init; } // Наглядный формат даты (например "2026-07-30 08:23:48")

    public string? Event { get; init; }

    public string? User { get; init; }

    [JsonPropertyName("UserUuid")]
    public string? UserUuid { get; init; }

    [JsonPropertyName("Metadata")]
    public string? Meta { get; init; }

    [JsonPropertyName("MetadataUuid")]
    public string? MetadataUuid { get; init; }

    public string? Tran { get; init; }

    [JsonPropertyName("TransactionDate")]
    [JsonConverter(typeof(ClickHouseNullableDateTimeConverter))]
    public DateTime? TransactionDate { get; init; }

    [JsonPropertyName("TransactionNumber")]
    public long TransactionNumber { get; init; }

    [JsonPropertyName("Application")]
    public string? App { get; init; }

    public string? Comment { get; init; }

    [JsonPropertyName("Severity")]
    public string? Importance { get; init; }

    public string? Data { get; init; }

    public string? DataPresentation { get; init; }

    public string? Computer { get; init; }

    public string? Server { get; init; }

    public string? Connection { get; init; }

    [JsonPropertyName("MainPort")]
    public string? Port { get; init; }

    [JsonPropertyName("AddPort")]
    public string? AddPort { get; init; }

    public string? Session { get; init; }

    [JsonPropertyName("TransactionStatus")]
    public string? TranStatusText { get; init; }

    public string? AppTypeName { get; init; }

    public string? FileName { get; init; }

    public long FileSize { get; init; }

    public string? FileSizeFormatted { get; init; }
}

/// <summary>
/// Модель структурированного документа записи Технологического Журнала 1С для экспорта.
/// </summary>
public sealed record TechLogDoc
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("DateTime")]
    [JsonConverter(typeof(ClickHouseDateTimeConverter))]
    public required DateTime Date { get; init; }

    /// <summary>
    /// Совместимость с существующими таблицами ClickHouse, где физическая колонка ключа партиционирования называется Date.
    /// </summary>
    [JsonPropertyName("Date")]
    [JsonConverter(typeof(ClickHouseDateTimeConverter))]
    public DateTime DateLegacy => Date;

    public required string DateFormatted { get; init; } // Наглядный формат даты (например "2026-07-30 08:23:48.384")

    public required long Duration { get; init; } // Длительность в микросекундах (мкс)

    public required double DurationMs { get; init; } // Длительность в миллисекундах (мс)

    public required double DurationSec { get; init; } // Длительность в секундах (с)

    public required string DurationFormatted { get; init; } // Наглядная строка длительности (например "31.98 ms")

    public required string Event { get; init; }

    public required int Level { get; init; }

    public string? ProcessName { get; init; }

    public string? ProcessId { get; init; }

    public string? Spid { get; init; }            // Идентификатор серверного процесса / потока СУБД (spid / dbpid)

    public string? OSThread { get; init; }        // Идентификатор потока ОС (OSThread)

    public string? SessionId { get; init; }       // Номер сеанса 1С (SessionID / t_clientID)

    public string? LongInfoName { get; init; }    // Целевое действие для LONGDURATIONINFO (DBMSSQL, CALL, TLOCK и др.)

    public long? LongInfoWait { get; init; }      // Время выполнения на момент среза (мкс)

    public string? User { get; init; }

    [JsonPropertyName("Application")]
    public string? App { get; init; }

    public string? ConnectId { get; init; }

    public string? ClientId { get; init; }

    public string? Context { get; init; }

    public string? Sql { get; init; }

    // Поля 100% паритета с Magnit pipeline.json
    public string? Locks { get; init; }          // Данные о блокировках ресурсов (TLOCK/TDEADLOCK)

    public string? WaitConnections { get; init; } // Ожидающие соединения при блокировках (TTIMEOUT/TDEADLOCK)

    public string? LkSrc { get; init; }           // Идентификатор источника блокировки

    public string? Descr { get; init; }           // Полный текст описания ошибки (EXCP / EXCPCNTX)

    public long? Rows { get; init; }              // Количество обрабатываемых строк СУБД

    public long? InBytes { get; init; }           // Входящий сетевой/HTTP трафик (байт)

    public long? OutBytes { get; init; }          // Исходящий сетевой/HTTP трафик (байт)

    public string? Method { get; init; }          // Метод REST / HTTP (GET, POST и т.д.)

    public string? Url { get; init; }             // Вызываемый URI / URL адрес

    public Dictionary<string, string> Properties { get; init; } = [];

    /// <summary>
    /// Флаг активного (незавершенного, длящегося на момент записи) события (LONGDURATIONINFO).
    /// </summary>
    [JsonIgnore]
    public bool IsActiveOperation => string.Equals(Event, "LONGDURATIONINFO", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(LongInfoName)
        || LongInfoWait.HasValue;

    /// <summary>
    /// Статус выполнения операции (Выполняется / Завершено).
    /// </summary>
    [JsonIgnore]
    public string ExecutionStatus => IsActiveOperation ? "Выполняется" : "Завершено";
}

/// <summary>
/// Словарь данных Журнала Регистрации (пользователи, приложения, события, метаданные).
/// </summary>
public sealed class LgfDictionary
{
    public Dictionary<string, string> Users { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> UserUuids { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Computers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Apps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Metas { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> MetaUuids { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Servers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Ports { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SecondaryPorts { get; } = new(StringComparer.Ordinal);

    private const int MaxSessionCacheCapacity = 50_000;

    /// <summary>
    /// Динамический кэш привязки сессий к пользователям (SessionID -> UserName).
    /// Автоматически подставляет имя пользователя в транзакции и события данных того же сеанса 1С.
    /// </summary>
    public Dictionary<string, string> SessionUsers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Динамический кэш привязки сессий к UUID пользователей (SessionID -> UserUuid).
    /// </summary>
    public Dictionary<string, string> SessionUserUuids { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Динамический кэш привязки сессий к рабочим станциям (SessionID -> Workstation).
    /// </summary>
    public Dictionary<string, string> SessionComputers { get; } = new(StringComparer.Ordinal);

    public void TrackSessionUser(string session, string user, string? userUuid = null)
    {
        if (SessionUsers.Count >= MaxSessionCacheCapacity && !SessionUsers.ContainsKey(session))
        {
            SessionUsers.Clear();
            SessionUserUuids.Clear();
        }
        SessionUsers[session] = user;
        if (!string.IsNullOrEmpty(userUuid))
        {
            SessionUserUuids[session] = userUuid;
        }
    }

    public void TrackSessionComputer(string session, string computer)
    {
        if (SessionComputers.Count >= MaxSessionCacheCapacity && !SessionComputers.ContainsKey(session))
        {
            SessionComputers.Clear();
        }
        SessionComputers[session] = computer;
    }
}

/// <summary>
/// Главные параметры конфигурации службы экспорта.
/// </summary>
public sealed class ExporterOptions
{
    public const string SectionName = "Exporter";

    public int PollingIntervalSeconds { get; set; } = 30;
    public string StateFilePath { get; set; } = "state.json";
    public FileDumpSettings FileDump { get; set; } = new();
    public EventLogSettings EventLog { get; set; } = new();
    public TechLogSettings TechLog { get; set; } = new();
    public ElasticSettings Elastic { get; set; } = new();
    public KibanaSettings Kibana { get; set; } = new();
    public ClickHouseSettings ClickHouse { get; set; } = new();
}

/// <summary>
/// Настройки выгрузки распарсенных логов 1С в локальные JSON-файлы (JSONL / append-режим с ротацией по размеру и количеству).
/// </summary>
public sealed class FileDumpSettings
{
    public bool TechLogEnabled { get; set; } = true;
    public bool EventLogEnabled { get; set; } = true;

    [JsonIgnore]
    public bool IsTechLogActive => TechLogEnabled;

    [JsonIgnore]
    public bool IsEventLogActive => EventLogEnabled;

    /// <summary>
    /// Каталог выгрузки локальных JSON-дампов Технологического Журнала (если не задан, используется DirectoryPath/techlog либо C:/1C_Export/json_dump/techlog).
    /// </summary>
    public string TechLogDirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// Каталог выгрузки локальных JSON-дампов Журнала Регистрации (если не задан, используется DirectoryPath/eventlog либо C:/1C_Export/json_dump/eventlog).
    /// </summary>
    public string EventLogDirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// Устаревший базовый каталог выгрузки (сохранен для обратной совместимости старых конфигураций).
    /// </summary>
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// Шаблон маски имени файла дампа ТЖ (поддерживает макросы {N}, {PREFIX}). Настраивается в appsettings.json. Например: data_tglog_{N}.json
    /// </summary>
    public string TechLogFileNamePattern { get; set; } = "data_tglog_{N}.json";

    /// <summary>
    /// Шаблон маски имени файла дампа ЖР (поддерживает макросы {N}, {PREFIX}). Настраивается в appsettings.json. Например: data_evlog_{N}.json
    /// </summary>
    public string EventLogFileNamePattern { get; set; } = "data_evlog_{N}.json";

    /// <summary>
    /// Лимит количества хранящихся файлов/итераций дампов в каждом каталоге (по умолчанию 30 файлов, старые файлы автоматически удаляются).
    /// </summary>
    public int RetainedFileCountLimit { get; set; } = 30;

    /// <summary>
    /// Максимальный суммарный размер всех файлов дампа в каталоге в мегабайтах (по умолчанию 500 МБ).
    /// При превышении старейшие слоты/файлы автоматически удаляются.
    /// </summary>
    public int MaxTotalSizeMb { get; set; } = 500;

    /// <summary>
    /// Максимальный размер одного файла дампа в мегабайтах (0 = без лимита по размеру на один файл).
    /// </summary>
    public int MaxFileSizeMb { get; set; } = 0;

    /// <summary>
    /// Максимальное количество записей/строк в одном файле дампа (0 = без лимита по строкам).
    /// </summary>
    public int MaxFileRecordCount { get; set; } = 0;

    /// <summary>
    /// Стратегия ротации файлов дампов: "Size" (по размеру МБ), "RecordCount" (по числу записей), "SizeOrRecordCount" (по размеру или числу записей).
    /// </summary>
    public string RollStrategy { get; set; } = "SizeOrRecordCount";
}

/// <summary>
/// Настройки мониторинга Журнала Регистрации 1С.
/// </summary>
public sealed class EventLogSettings
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = string.Empty;
    /// <summary>
    /// Имя информационной базы в кластере 1С (например, "demo_db" или "accounting").
    /// При указании служба автоматически считывает реестр 1CV8Clst.lst в DirectoryPath (каталог кластера reg_1541),
    /// находит точный GUID базы и привязывается строго к её персональным файлам и словарю 1Cv8.lgf.
    /// Поддерживается перечисление нескольких баз через запятую.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;
    public string IndexId { get; set; } = "prod";
    public string Periodicity { get; set; } = "h"; // "h" (часовой) или "d" (дневной)
    public int HourDelta { get; set; } = 1;
    public string FileName { get; set; } = string.Empty;
    public bool LoadArchive { get; set; } = false; // false = только live-события с конца при первом запуске, true = читать всю историю с начала
    public string Separation { get; set; } = "Day"; // "Day" (D), "Month" (M), "Hour" (H), "None" (all)
    /// <summary>
    /// Режим прямой потоковой отправки батчей из оперативной памяти напрямую в целевые хранилища (ClickHouse/Elasticsearch).
    /// При true: данные из памяти сразу вставляются в БД (до 20 000+ строк/сек), а при включенном FileDump параллельно пишется дамп без ожидания диска.
    /// При false: двухэтапная схема (TwoStage) — сначала запись дампа на диск, затем чтение и транспортировка.
    /// </summary>
    public bool DirectStream { get; set; } = true;

    /// <summary>
    /// Фильтрация рутинных пустых событий транзакций (Транзакция. Начало и Транзакция. Фиксация).
    /// Исключает 97% служебного шума фоновых заданий и ускоряет догон в 35 раз.
    /// По умолчанию: true.
    /// </summary>
    public bool FilterEmptyTransactions { get; set; } = true;
}

/// <summary>
/// Настройки мониторинга Технологического Журнала 1С.
/// </summary>
public sealed class TechLogSettings
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = string.Empty;
    public string IndexId { get; set; } = "prod";
    public int MaxAgeHours { get; set; } = 24;
    public bool LoadArchive { get; set; } = false; // false = только live-события с конца при первом запуске, true = читать всю историю с начала
    public string Separation { get; set; } = "Day"; // "Day" (D), "Month" (M), "Hour" (H), "None" (all)
    /// <summary>
    /// Режим прямой потоковой отправки батчей из оперативной памяти напрямую в целевые хранилища (ClickHouse/Elasticsearch).
    /// При true: данные из памяти сразу вставляются в БД (до 20 000+ строк/сек), а при включенном FileDump параллельно пишется дамп без ожидания диска.
    /// При false: двухэтапная схема (TwoStage) — сначала запись дампа на диск, затем чтение и транспортировка.
    /// </summary>
    public bool DirectStream { get; set; } = true;

    /// <summary>
    /// Фильтрация пустых событий ТЖ с нулевой длительностью (0 мкс) и без контекста/ошибок.
    /// По умолчанию: true.
    /// </summary>
    public bool FilterEmptyEvents { get; set; } = true;
}

/// <summary>
/// Настройки подключения и авторизации Elasticsearch / OpenSearch.
/// </summary>
public sealed class ElasticSettings
{
    public bool TechLogEnabled { get; set; } = false;
    public bool EventLogEnabled { get; set; } = false;

    [JsonIgnore]
    public bool IsTechLogActive => TechLogEnabled;

    [JsonIgnore]
    public bool IsEventLogActive => EventLogEnabled;

    [JsonIgnore]
    public bool IsAnyEnabled => IsTechLogActive || IsEventLogActive;

    public string ServerUrl { get; set; } = "http://localhost:9200";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? ApiKey { get; set; }
    public string EventLogIndexPrefix { get; set; } = "events";
    public string TechLogIndexPrefix { get; set; } = "techlog";
    public string Separation { get; set; } = "Day"; // Гранулярность индексов по умолчанию: "Day" (D), "Month" (M), "Hour" (H), "None" (all)
    public bool AutoApplyTemplate { get; set; } = false; // Автоприменение шаблона при старте службы (по умолчанию выключено)
    public int BulkBatchSize { get; set; } = 1000;
}

/// <summary>
/// Вспомогательный класс вычисления суффикса и имени индекса с учетом гранулярности разделения (Separation).
/// </summary>
public static class IndexNamingHelper
{
    public static string BuildTimeSuffix(string? separation, DateTime timestamp)
    {
        return (separation ?? "Day").Trim().ToUpperInvariant() switch
        {
            "M" or "MONTH" or "MONTHLY" => timestamp.ToString("yyyyMM"),
            "H" or "HOUR" or "HOURLY" => timestamp.ToString("yyyyMMddHH"),
            "ALL" or "NONE" => "all",
            _ => timestamp.ToString("yyyyMMdd")
        };
    }

    public static string BuildIndexName(string prefix, string indexId, string? separation, DateTime timestamp)
    {
        var suffix = BuildTimeSuffix(separation, timestamp);
        return string.IsNullOrWhiteSpace(indexId)
            ? $"{prefix}_{suffix}"
            : $"{prefix}_{indexId}_{suffix}";
    }
}

/// <summary>
/// Настройки веб-интерфейса Kibana / OpenSearch Dashboards.
/// </summary>
public sealed class KibanaSettings
{
    public bool TechLogEnabled { get; set; } = false;
    public bool EventLogEnabled { get; set; } = false;

    [JsonIgnore]
    public bool IsTechLogActive => TechLogEnabled;

    [JsonIgnore]
    public bool IsEventLogActive => EventLogEnabled;

    [JsonIgnore]
    public bool IsAnyEnabled => IsTechLogActive || IsEventLogActive;

    public string ServerUrl { get; set; } = "http://localhost:5601";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string IndexPatternEventLog { get; set; } = "events_*";
    public string IndexPatternTechLog { get; set; } = "techlog_*";
}

/// <summary>
/// Настройки подключения и экспорта в ClickHouse через HTTP API (FORMAT JSONEachRow).
/// </summary>
public sealed class ClickHouseSettings
{
    public bool TechLogEnabled { get; set; } = false;
    public bool EventLogEnabled { get; set; } = false;

    [JsonIgnore]
    public bool IsTechLogActive => TechLogEnabled;

    [JsonIgnore]
    public bool IsEventLogActive => EventLogEnabled;

    [JsonIgnore]
    public bool IsAnyEnabled => IsTechLogActive || IsEventLogActive;

    public string ServerUrl { get; set; } = "http://localhost:8123";
    public string Database { get; set; } = "default";
    public string? User { get; set; } = "default";
    public string? Password { get; set; } = "";
    public string TechLogTable { get; set; } = "techlog";
    public string EventLogTable { get; set; } = "eventlog";
    public int BulkBatchSize { get; set; } = 25000;
    public int TimeoutSeconds { get; set; } = 60;
}
