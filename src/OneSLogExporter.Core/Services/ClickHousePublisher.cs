using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Serialization;

namespace OneSLogExporter.Core.Services;

/// <summary>
/// Высокопроизводительный сервис потоковой публикации документов в ClickHouse через HTTP API (FORMAT JSONEachRow).
/// Поддерживает автоматическое создание баз данных, таблиц MergeTree, маппинг Map(String, String) и батчевую отправку.
/// </summary>
public sealed class ClickHousePublisher : IDisposable
{
    private readonly ClickHouseSettings _settings;
    private readonly ILogger<ClickHousePublisher> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private bool _techLogTableEnsured;
    private bool _eventLogTableEnsured;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);

    public ClickHousePublisher(ClickHouseSettings settings, ILogger<ClickHousePublisher> logger, HttpClient? httpClient = null)
    {
        _settings = settings;
        _logger = logger;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, settings.TimeoutSeconds))
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds))
            };
            _disposeClient = true;
        }
    }

    public void ResetSchemaCache()
    {
        _techLogTableEnsured = false;
        _eventLogTableEnsured = false;
    }

    /// <summary>
    /// Нормализация URL сервера ClickHouse с поддержкой голого хоста/IP (например, "192.168.1.10" -> "http://192.168.1.10:8123").
    /// </summary>
    public static string NormalizeServerUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return "http://localhost:8123";
        var trimmed = rawUrl.Trim();
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.IsDefaultPort && !rawUrl.Contains($":{uri.Port}", StringComparison.Ordinal))
            {
                var builder = new UriBuilder(uri)
                {
                    Port = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 8443 : 8123
                };
                return builder.Uri.ToString().TrimEnd('/');
            }
        }

        return trimmed.TrimEnd('/');
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string query, HttpContent? content = null)
    {
        var baseUrl = NormalizeServerUrl(_settings.ServerUrl);
        var db = string.IsNullOrWhiteSpace(_settings.Database) ? "default" : _settings.Database;
        var url = $"{baseUrl}/?database={Uri.EscapeDataString(db)}&query={Uri.EscapeDataString(query)}";

        var req = new HttpRequestMessage(method, url)
        {
            Content = content
        };

        if (!string.IsNullOrWhiteSpace(_settings.User))
        {
            req.Headers.Add("X-ClickHouse-User", _settings.User);
            if (!string.IsNullOrEmpty(_settings.Password))
            {
                req.Headers.Add("X-ClickHouse-Key", _settings.Password);
            }
        }

        return req;
    }

    /// <summary>
    /// Автоматическое создание таблицы Технологического Журнала (MergeTree) в ClickHouse при её отсутствии.
    /// </summary>
    public async ValueTask EnsureTechLogTableAsync(CancellationToken ct = default)
    {
        if (_techLogTableEnsured) return;

        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_techLogTableEnsured) return;

            var db = string.IsNullOrWhiteSpace(_settings.Database) ? "default" : _settings.Database;
            var table = string.IsNullOrWhiteSpace(_settings.TechLogTable) ? "techlog" : _settings.TechLogTable;

            var createDbSql = $"CREATE DATABASE IF NOT EXISTS {db}";
            using (var dbReq = CreateRequest(HttpMethod.Post, createDbSql))
            {
                var dbResp = await _httpClient.SendAsync(dbReq, ct).ConfigureAwait(false);
                if (!dbResp.IsSuccessStatusCode)
                {
                    var err = await dbResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning("Предупреждение ClickHouse при CREATE DATABASE {Db}: {Err}", db, err);
                }
            }

            var createTableSql = $"""
                CREATE TABLE IF NOT EXISTS {db}.{table} (
                    id String,
                    Date DateTime('UTC'),
                    DateFormatted LowCardinality(String),
                    Duration Int64,
                    DurationMs Float64,
                    DurationSec Float64,
                    DurationFormatted LowCardinality(String),
                    Event LowCardinality(String),
                    Level Int32,
                    ProcessName LowCardinality(String),
                    ProcessId LowCardinality(String),
                    User LowCardinality(String),
                    App LowCardinality(String),
                    ConnectId LowCardinality(String),
                    ClientId LowCardinality(String),
                    Context String,
                    Sql String,
                    Locks String,
                    WaitConnections String,
                    LkSrc LowCardinality(String),
                    Descr String,
                    Rows Int64,
                    InBytes Int64,
                    OutBytes Int64,
                    Method LowCardinality(String),
                    Url String,
                    Properties Map(String, String),
                    FileName LowCardinality(String),
                    FileSize Int64,
                    FileSizeFormatted LowCardinality(String)
                ) ENGINE = MergeTree()
                PARTITION BY toYYYYMM(Date)
                ORDER BY (Event, Date, id)
                """;

            using (var tableReq = CreateRequest(HttpMethod.Post, createTableSql))
            {
                var tableResp = await _httpClient.SendAsync(tableReq, ct).ConfigureAwait(false);
                if (!tableResp.IsSuccessStatusCode)
                {
                    var err = await tableResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogError("Ошибка создания таблицы ClickHouse {Db}.{Table}: {Err}", db, table, err);
                    return;
                }
            }

            _techLogTableEnsured = true;
            _logger.LogInformation("Таблица ClickHouse {Db}.{Table} готова к приему данных.", db, table);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Исключение при инициализации таблицы ClickHouse ТЖ");
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>
    /// Автоматическое создание таблицы Журнала Регистрации (MergeTree) в ClickHouse при её отсутствии.
    /// </summary>
    public async ValueTask EnsureEventLogTableAsync(CancellationToken ct = default)
    {
        if (_eventLogTableEnsured) return;

        await _schemaLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_eventLogTableEnsured) return;

            var db = string.IsNullOrWhiteSpace(_settings.Database) ? "default" : _settings.Database;
            var table = string.IsNullOrWhiteSpace(_settings.EventLogTable) ? "eventlog" : _settings.EventLogTable;

            var createDbSql = $"CREATE DATABASE IF NOT EXISTS {db}";
            using (var dbReq = CreateRequest(HttpMethod.Post, createDbSql))
            {
                var dbResp = await _httpClient.SendAsync(dbReq, ct).ConfigureAwait(false);
                if (!dbResp.IsSuccessStatusCode)
                {
                    var err = await dbResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning("Предупреждение ClickHouse при CREATE DATABASE {Db}: {Err}", db, err);
                }
            }

            var createTableSql = $"""
                CREATE TABLE IF NOT EXISTS {db}.{table} (
                    id String,
                    DateTime DateTime('UTC'),
                    DateFormatted LowCardinality(String),
                    Event LowCardinality(String),
                    User LowCardinality(String),
                    UserUuid LowCardinality(String),
                    Metadata LowCardinality(String),
                    MetadataUuid LowCardinality(String),
                    Tran LowCardinality(String),
                    TransactionDate DateTime('UTC'),
                    TransactionNumber Int64,
                    Application LowCardinality(String),
                    Comment String,
                    Severity LowCardinality(String),
                    Data String,
                    DataPresentation String,
                    Computer LowCardinality(String),
                    Server LowCardinality(String),
                    Connection LowCardinality(String),
                    MainPort LowCardinality(String),
                    AddPort LowCardinality(String),
                    Session LowCardinality(String),
                    TransactionStatus LowCardinality(String),
                    AppTypeName LowCardinality(String),
                    FileName LowCardinality(String),
                    FileSize Int64,
                    FileSizeFormatted LowCardinality(String)
                ) ENGINE = MergeTree()
                PARTITION BY toYYYYMM(DateTime)
                ORDER BY (Event, DateTime, id)
                """;

            using (var tableReq = CreateRequest(HttpMethod.Post, createTableSql))
            {
                var tableResp = await _httpClient.SendAsync(tableReq, ct).ConfigureAwait(false);
                if (!tableResp.IsSuccessStatusCode)
                {
                    var err = await tableResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogError("Ошибка создания таблицы ClickHouse {Db}.{Table}: {Err}", db, table, err);
                    return;
                }
            }

            _eventLogTableEnsured = true;
            _logger.LogInformation("Таблица ClickHouse {Db}.{Table} готова к приему данных.", db, table);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Исключение при инициализации таблицы ClickHouse ЖР");
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>
    /// Массовая отправка документов Технологического Журнала в ClickHouse пачками.
    /// </summary>
    public async ValueTask<(int Success, int Failed)> BulkInsertTechLogAsync(IEnumerable<TechLogDoc> docs, CancellationToken ct = default)
    {
        var docList = docs as IReadOnlyList<TechLogDoc> ?? docs.ToList();
        if (docList.Count == 0) return (0, 0);

        await EnsureTechLogTableAsync(ct).ConfigureAwait(false);

        var batchSize = _settings.BulkBatchSize > 0 ? _settings.BulkBatchSize : 5000;
        var totalSuccess = 0;
        var totalFailed = 0;

        var db = string.IsNullOrWhiteSpace(_settings.Database) ? "default" : _settings.Database;
        var table = string.IsNullOrWhiteSpace(_settings.TechLogTable) ? "techlog" : _settings.TechLogTable;
        var insertQuery = $"INSERT INTO {db}.{table} SETTINGS input_format_skip_unknown_fields=1, input_format_null_as_default=1, date_time_input_format='best_effort' FORMAT JSONEachRow";

        for (var i = 0; i < docList.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, docList.Count - i);

            using var ms = new MemoryStream(65536);
            for (var j = 0; j < count; j++)
            {
                JsonSerializer.Serialize(ms, docList[i + j], LogJsonContext.Compact.TechLogDoc);
                ms.WriteByte((byte)'\n');
            }

            ms.Position = 0;
            using var content = new StreamContent(ms);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");

            using var req = CreateRequest(HttpMethod.Post, insertQuery, content);
            try
            {
                using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    totalSuccess += count;
                    _logger.LogInformation("ClickHouse: успешно отправлена пачка ТЖ ({Count} записей) в {Db}.{Table}.", count, db, table);
                }
                else
                {
                    var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogError("Ошибка вставки пачки ТЖ в ClickHouse ({StatusCode}): {Err}", (int)resp.StatusCode, err);
                    totalFailed += count;
                    throw new HttpRequestException($"Ошибка вставки пачки ТЖ в ClickHouse (HTTP {(int)resp.StatusCode}): {err}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой сетевого запроса при вставке в ClickHouse ТЖ");
                totalFailed += count;
                throw;
            }
        }

        return (totalSuccess, totalFailed);
    }

    /// <summary>
    /// Массовая отправка документов Журнала Регистрации в ClickHouse пачками.
    /// </summary>
    public async ValueTask<(int Success, int Failed)> BulkInsertEventLogAsync(IEnumerable<EventLogDoc> docs, CancellationToken ct = default)
    {
        var docList = docs as IReadOnlyList<EventLogDoc> ?? docs.ToList();
        if (docList.Count == 0) return (0, 0);

        await EnsureEventLogTableAsync(ct).ConfigureAwait(false);

        var batchSize = _settings.BulkBatchSize > 0 ? _settings.BulkBatchSize : 5000;
        var totalSuccess = 0;
        var totalFailed = 0;

        var db = string.IsNullOrWhiteSpace(_settings.Database) ? "default" : _settings.Database;
        var table = string.IsNullOrWhiteSpace(_settings.EventLogTable) ? "eventlog" : _settings.EventLogTable;
        var insertQuery = $"INSERT INTO {db}.{table} SETTINGS input_format_skip_unknown_fields=1, input_format_null_as_default=1, date_time_input_format='best_effort' FORMAT JSONEachRow";

        for (var i = 0; i < docList.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, docList.Count - i);

            using var ms = new MemoryStream(65536);
            for (var j = 0; j < count; j++)
            {
                JsonSerializer.Serialize(ms, docList[i + j], LogJsonContext.Compact.EventLogDoc);
                ms.WriteByte((byte)'\n');
            }

            ms.Position = 0;
            using var content = new StreamContent(ms);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");

            using var req = CreateRequest(HttpMethod.Post, insertQuery, content);
            try
            {
                using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    totalSuccess += count;
                    _logger.LogInformation("ClickHouse: успешно отправлена пачка ЖР ({Count} записей) в {Db}.{Table}.", count, db, table);
                }
                else
                {
                    var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogError("Ошибка вставки пачки ЖР в ClickHouse ({StatusCode}): {Err}", (int)resp.StatusCode, err);
                    totalFailed += count;
                    throw new HttpRequestException($"Ошибка вставки пачки ЖР в ClickHouse (HTTP {(int)resp.StatusCode}): {err}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Сбой сетевого запроса при вставке в ClickHouse ЖР");
                totalFailed += count;
                throw;
            }
        }

        return (totalSuccess, totalFailed);
    }

    /// <summary>
    /// Проверка доступности и авторизации в ClickHouse (SELECT version()).
    /// </summary>
    public async ValueTask<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Post, "SELECT version()");
            using var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var version = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                return (true, $"Соединение успешно установлено! ClickHouse версия: {version}");
            }

            var err = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            return (false, $"ClickHouse вернул код {(int)resp.StatusCode}: {err}");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка подключения к ClickHouse ({_settings.ServerUrl}): {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
        _schemaLock.Dispose();
    }
}
