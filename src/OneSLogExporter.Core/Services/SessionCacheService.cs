using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;

namespace OneSLogExporter.Core.Services;

/// <summary>
/// Высокопроизводительный дисковый сессионный кэш (SQLite WAL).
/// Обеспечивает потоковый сброс миллионов записей Журнала Регистрации и Техжурнала
/// на локальный накопитель (SSD) для защиты оперативной памяти (RAM) и моментальной выборки.
/// </summary>
public sealed class SessionCacheService : IAsyncDisposable, IDisposable
{
    private readonly string _dbFilePath;
    private readonly SqliteConnection _connection;
    private readonly bool _isEventLog;
    private long _totalCount;
    private bool _isDisposed;

    public string DbFilePath => _dbFilePath;
    public long TotalCount => _totalCount;

    private SessionCacheService(string dbFilePath, bool isEventLog)
    {
        _dbFilePath = dbFilePath;
        _isEventLog = isEventLog;

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        _connection = new SqliteConnection(csb.ConnectionString);
        _connection.Open();

        ConfigurePragmas();
        InitializeSchema();
    }

    /// <summary>
    /// Создание нового сессионного кэша для Журнала Регистрации.
    /// </summary>
    public static SessionCacheService CreateEventLogCache(string? tempDir = null)
    {
        var dir = tempDir ?? GetDefaultTempDirectory();
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, $"session_lg_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.db");
        return new SessionCacheService(dbPath, isEventLog: true);
    }

    /// <summary>
    /// Создание нового сессионного кэша для Технологического Журнала.
    /// </summary>
    public static SessionCacheService CreateTechLogCache(string? tempDir = null)
    {
        var dir = tempDir ?? GetDefaultTempDirectory();
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, $"session_tg_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.db");
        return new SessionCacheService(dbPath, isEventLog: false);
    }

    /// <summary>
    /// Получение стандартного каталога временных файлов приложения (./temp или %TEMP%).
    /// </summary>
    public static string GetDefaultTempDirectory()
    {
        var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        try
        {
            Directory.CreateDirectory(baseDir);
            var testFile = Path.Combine(baseDir, $".test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            return baseDir;
        }
        catch
        {
            var fallback = Path.Combine(Path.GetTempPath(), "OneSLogExporter", "temp");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private void ConfigurePragmas()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = OFF;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -64000;
            PRAGMA locking_mode = NORMAL;
            """;
        cmd.ExecuteNonQuery();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        if (_isEventLog)
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS event_logs (
                    id TEXT PRIMARY KEY,
                    date TEXT NOT NULL,
                    date_formatted TEXT,
                    event TEXT,
                    user TEXT,
                    meta TEXT,
                    tran TEXT,
                    app TEXT,
                    comment TEXT,
                    importance TEXT,
                    data TEXT,
                    data_presentation TEXT,
                    computer TEXT,
                    server TEXT,
                    connection TEXT,
                    port TEXT,
                    session TEXT,
                    tran_status TEXT,
                    app_type TEXT,
                    file_name TEXT,
                    file_size INTEGER,
                    file_size_formatted TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_el_date ON event_logs(date);
                CREATE INDEX IF NOT EXISTS idx_el_importance ON event_logs(importance);
                CREATE INDEX IF NOT EXISTS idx_el_event ON event_logs(event);
                CREATE INDEX IF NOT EXISTS idx_el_user ON event_logs(user);
                """;
        }
        else
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS tech_logs (
                    id TEXT PRIMARY KEY,
                    date TEXT NOT NULL,
                    date_formatted TEXT,
                    duration INTEGER,
                    duration_ms REAL,
                    duration_sec REAL,
                    duration_formatted TEXT,
                    event TEXT NOT NULL,
                    level INTEGER,
                    process_name TEXT,
                    process_id TEXT,
                    spid TEXT,
                    os_thread TEXT,
                    session_id TEXT,
                    long_info_name TEXT,
                    long_info_wait INTEGER,
                    user TEXT,
                    app TEXT,
                    connect_id TEXT,
                    client_id TEXT,
                    sql TEXT,
                    rows INTEGER,
                    context TEXT,
                    locks TEXT,
                    wait_connections TEXT,
                    lksrc TEXT,
                    descr TEXT,
                    in_bytes INTEGER,
                    out_bytes INTEGER,
                    method TEXT,
                    url TEXT,
                    props TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_tl_date ON tech_logs(date);
                CREATE INDEX IF NOT EXISTS idx_tl_event ON tech_logs(event);
                CREATE INDEX IF NOT EXISTS idx_tl_duration ON tech_logs(duration);
                CREATE INDEX IF NOT EXISTS idx_tl_user ON tech_logs(user);
                """;
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Высокопроизводительная пакетная вставка записей ЖР в SQLite кэш (250 000+ записей/сек).
    /// </summary>
    public async ValueTask InsertEventLogsAsync(IReadOnlyList<EventLogDoc> batch, CancellationToken ct = default)
    {
        if (batch.Count == 0 || _isDisposed) return;

        await using var tx = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;

        cmd.CommandText = """
            INSERT OR IGNORE INTO event_logs (
                id, date, date_formatted, event, user, meta, tran, app, comment,
                importance, data, data_presentation, computer, server, connection,
                port, session, tran_status, app_type, file_name, file_size, file_size_formatted
            ) VALUES (
                $id, $date, $date_formatted, $event, $user, $meta, $tran, $app, $comment,
                $importance, $data, $data_presentation, $computer, $server, $connection,
                $port, $session, $tran_status, $app_type, $file_name, $file_size, $file_size_formatted
            )
            """;

        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pDate = cmd.Parameters.Add("$date", SqliteType.Text);
        var pDateFormatted = cmd.Parameters.Add("$date_formatted", SqliteType.Text);
        var pEvent = cmd.Parameters.Add("$event", SqliteType.Text);
        var pUser = cmd.Parameters.Add("$user", SqliteType.Text);
        var pMeta = cmd.Parameters.Add("$meta", SqliteType.Text);
        var pTran = cmd.Parameters.Add("$tran", SqliteType.Text);
        var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
        var pComment = cmd.Parameters.Add("$comment", SqliteType.Text);
        var pImportance = cmd.Parameters.Add("$importance", SqliteType.Text);
        var pData = cmd.Parameters.Add("$data", SqliteType.Text);
        var pDataPres = cmd.Parameters.Add("$data_presentation", SqliteType.Text);
        var pComputer = cmd.Parameters.Add("$computer", SqliteType.Text);
        var pServer = cmd.Parameters.Add("$server", SqliteType.Text);
        var pConnection = cmd.Parameters.Add("$connection", SqliteType.Text);
        var pPort = cmd.Parameters.Add("$port", SqliteType.Text);
        var pSession = cmd.Parameters.Add("$session", SqliteType.Text);
        var pTranStatus = cmd.Parameters.Add("$tran_status", SqliteType.Text);
        var pAppType = cmd.Parameters.Add("$app_type", SqliteType.Text);
        var pFileName = cmd.Parameters.Add("$file_name", SqliteType.Text);
        var pFileSize = cmd.Parameters.Add("$file_size", SqliteType.Integer);
        var pFileSizeFormatted = cmd.Parameters.Add("$file_size_formatted", SqliteType.Text);

        foreach (var doc in batch)
        {
            if (ct.IsCancellationRequested) break;

            pId.Value = doc.Id;
            pDate.Value = doc.Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            pDateFormatted.Value = doc.DateFormatted;
            pEvent.Value = (object?)doc.Event ?? DBNull.Value;
            pUser.Value = (object?)doc.User ?? DBNull.Value;
            pMeta.Value = (object?)doc.Meta ?? DBNull.Value;
            pTran.Value = (object?)doc.Tran ?? DBNull.Value;
            pApp.Value = (object?)doc.App ?? DBNull.Value;
            pComment.Value = (object?)doc.Comment ?? DBNull.Value;
            pImportance.Value = (object?)doc.Importance ?? DBNull.Value;
            pData.Value = (object?)doc.Data ?? DBNull.Value;
            pDataPres.Value = (object?)doc.DataPresentation ?? DBNull.Value;
            pComputer.Value = (object?)doc.Computer ?? DBNull.Value;
            pServer.Value = (object?)doc.Server ?? DBNull.Value;
            pConnection.Value = (object?)doc.Connection ?? DBNull.Value;
            pPort.Value = (object?)doc.Port ?? DBNull.Value;
            pSession.Value = (object?)doc.Session ?? DBNull.Value;
            pTranStatus.Value = (object?)doc.TranStatusText ?? DBNull.Value;
            pAppType.Value = (object?)doc.AppTypeName ?? DBNull.Value;
            pFileName.Value = (object?)doc.FileName ?? DBNull.Value;
            pFileSize.Value = doc.FileSize;
            pFileSizeFormatted.Value = (object?)doc.FileSizeFormatted ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        Interlocked.Add(ref _totalCount, batch.Count);
    }

    /// <summary>
    /// Высокопроизводительная пакетная вставка записей ТЖ в SQLite кэш (250 000+ записей/сек).
    /// </summary>
    public async ValueTask InsertTechLogsAsync(IReadOnlyList<TechLogDoc> batch, CancellationToken ct = default)
    {
        if (batch.Count == 0 || _isDisposed) return;

        await using var tx = await _connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = _connection.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;

        cmd.CommandText = """
            INSERT OR IGNORE INTO tech_logs (
                id, date, date_formatted, duration, duration_ms, duration_sec, duration_formatted,
                event, level, process_name, process_id, spid, os_thread, session_id,
                long_info_name, long_info_wait, user, app, connect_id, client_id,
                sql, rows, context, locks, wait_connections, lksrc, descr,
                in_bytes, out_bytes, method, url, props
            ) VALUES (
                $id, $date, $date_formatted, $duration, $duration_ms, $duration_sec, $duration_formatted,
                $event, $level, $process_name, $process_id, $spid, $os_thread, $session_id,
                $long_info_name, $long_info_wait, $user, $app, $connect_id, $client_id,
                $sql, $rows, $context, $locks, $wait_connections, $lksrc, $descr,
                $in_bytes, $out_bytes, $method, $url, $props
            )
            """;

        var pId = cmd.Parameters.Add("$id", SqliteType.Text);
        var pDate = cmd.Parameters.Add("$date", SqliteType.Text);
        var pDateFormatted = cmd.Parameters.Add("$date_formatted", SqliteType.Text);
        var pDuration = cmd.Parameters.Add("$duration", SqliteType.Integer);
        var pDurationMs = cmd.Parameters.Add("$duration_ms", SqliteType.Real);
        var pDurationSec = cmd.Parameters.Add("$duration_sec", SqliteType.Real);
        var pDurationFormatted = cmd.Parameters.Add("$duration_formatted", SqliteType.Text);
        var pEvent = cmd.Parameters.Add("$event", SqliteType.Text);
        var pLevel = cmd.Parameters.Add("$level", SqliteType.Integer);
        var pProcessName = cmd.Parameters.Add("$process_name", SqliteType.Text);
        var pProcessId = cmd.Parameters.Add("$process_id", SqliteType.Text);
        var pSpid = cmd.Parameters.Add("$spid", SqliteType.Text);
        var pOSThread = cmd.Parameters.Add("$os_thread", SqliteType.Text);
        var pSessionId = cmd.Parameters.Add("$session_id", SqliteType.Text);
        var pLongInfoName = cmd.Parameters.Add("$long_info_name", SqliteType.Text);
        var pLongInfoWait = cmd.Parameters.Add("$long_info_wait", SqliteType.Integer);
        var pUser = cmd.Parameters.Add("$user", SqliteType.Text);
        var pApp = cmd.Parameters.Add("$app", SqliteType.Text);
        var pConnectId = cmd.Parameters.Add("$connect_id", SqliteType.Text);
        var pClientId = cmd.Parameters.Add("$client_id", SqliteType.Text);
        var pSql = cmd.Parameters.Add("$sql", SqliteType.Text);
        var pRows = cmd.Parameters.Add("$rows", SqliteType.Integer);
        var pContext = cmd.Parameters.Add("$context", SqliteType.Text);
        var pLocks = cmd.Parameters.Add("$locks", SqliteType.Text);
        var pWaitConn = cmd.Parameters.Add("$wait_connections", SqliteType.Text);
        var pLkSrc = cmd.Parameters.Add("$lksrc", SqliteType.Text);
        var pDescr = cmd.Parameters.Add("$descr", SqliteType.Text);
        var pInBytes = cmd.Parameters.Add("$in_bytes", SqliteType.Integer);
        var pOutBytes = cmd.Parameters.Add("$out_bytes", SqliteType.Integer);
        var pMethod = cmd.Parameters.Add("$method", SqliteType.Text);
        var pUrl = cmd.Parameters.Add("$url", SqliteType.Text);
        var pProps = cmd.Parameters.Add("$props", SqliteType.Text);

        foreach (var doc in batch)
        {
            if (ct.IsCancellationRequested) break;

            pId.Value = doc.Id;
            pDate.Value = doc.Date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            pDateFormatted.Value = doc.DateFormatted;
            pDuration.Value = doc.Duration;
            pDurationMs.Value = doc.DurationMs;
            pDurationSec.Value = doc.DurationSec;
            pDurationFormatted.Value = doc.DurationFormatted;
            pEvent.Value = doc.Event;
            pLevel.Value = doc.Level;
            pProcessName.Value = (object?)doc.ProcessName ?? DBNull.Value;
            pProcessId.Value = (object?)doc.ProcessId ?? DBNull.Value;
            pSpid.Value = (object?)doc.Spid ?? DBNull.Value;
            pOSThread.Value = (object?)doc.OSThread ?? DBNull.Value;
            pSessionId.Value = (object?)doc.SessionId ?? DBNull.Value;
            pLongInfoName.Value = (object?)doc.LongInfoName ?? DBNull.Value;
            pLongInfoWait.Value = (object?)doc.LongInfoWait ?? DBNull.Value;
            pUser.Value = (object?)doc.User ?? DBNull.Value;
            pApp.Value = (object?)doc.App ?? DBNull.Value;
            pConnectId.Value = (object?)doc.ConnectId ?? DBNull.Value;
            pClientId.Value = (object?)doc.ClientId ?? DBNull.Value;
            pSql.Value = (object?)doc.Sql ?? DBNull.Value;
            pRows.Value = (object?)doc.Rows ?? DBNull.Value;
            pContext.Value = (object?)doc.Context ?? DBNull.Value;
            pLocks.Value = (object?)doc.Locks ?? DBNull.Value;
            pWaitConn.Value = (object?)doc.WaitConnections ?? DBNull.Value;
            pLkSrc.Value = (object?)doc.LkSrc ?? DBNull.Value;
            pDescr.Value = (object?)doc.Descr ?? DBNull.Value;
            pInBytes.Value = (object?)doc.InBytes ?? DBNull.Value;
            pOutBytes.Value = (object?)doc.OutBytes ?? DBNull.Value;
            pMethod.Value = (object?)doc.Method ?? DBNull.Value;
            pUrl.Value = (object?)doc.Url ?? DBNull.Value;
            pProps.Value = doc.Properties is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(doc.Properties) : DBNull.Value;

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        Interlocked.Add(ref _totalCount, batch.Count);
    }

    /// <summary>
    /// Потоковое чтение всех документов ЖР из кэша для экспорта в Excel/JSON/Elastic без OOM.
    /// </summary>
    public async IAsyncEnumerable<EventLogDoc> StreamAllEventLogsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_isDisposed) yield break;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM event_logs ORDER BY date ASC";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return MapEventLogDoc(reader);
        }
    }

    /// <summary>
    /// Потоковое чтение всех документов ТЖ из кэша для экспорта в Excel/JSON/Elastic без OOM.
    /// </summary>
    public async IAsyncEnumerable<TechLogDoc> StreamAllTechLogsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_isDisposed) yield break;

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM tech_logs ORDER BY date ASC";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return MapTechLogDoc(reader);
        }
    }

    private static EventLogDoc MapEventLogDoc(SqliteDataReader r)
    {
        var dateStr = r.GetString(r.GetOrdinal("date"));
        DateTime.TryParseExact(dateStr, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt);

        return new EventLogDoc
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Date = dt,
            DateFormatted = r.IsDBNull(r.GetOrdinal("date_formatted")) ? dateStr : r.GetString(r.GetOrdinal("date_formatted")),
            Event = r.IsDBNull(r.GetOrdinal("event")) ? null : r.GetString(r.GetOrdinal("event")),
            User = r.IsDBNull(r.GetOrdinal("user")) ? null : r.GetString(r.GetOrdinal("user")),
            Meta = r.IsDBNull(r.GetOrdinal("meta")) ? null : r.GetString(r.GetOrdinal("meta")),
            Tran = r.IsDBNull(r.GetOrdinal("tran")) ? null : r.GetString(r.GetOrdinal("tran")),
            App = r.IsDBNull(r.GetOrdinal("app")) ? null : r.GetString(r.GetOrdinal("app")),
            Comment = r.IsDBNull(r.GetOrdinal("comment")) ? null : r.GetString(r.GetOrdinal("comment")),
            Importance = r.IsDBNull(r.GetOrdinal("importance")) ? null : r.GetString(r.GetOrdinal("importance")),
            Data = r.IsDBNull(r.GetOrdinal("data")) ? null : r.GetString(r.GetOrdinal("data")),
            DataPresentation = r.IsDBNull(r.GetOrdinal("data_presentation")) ? null : r.GetString(r.GetOrdinal("data_presentation")),
            Computer = r.IsDBNull(r.GetOrdinal("computer")) ? null : r.GetString(r.GetOrdinal("computer")),
            Server = r.IsDBNull(r.GetOrdinal("server")) ? null : r.GetString(r.GetOrdinal("server")),
            Connection = r.IsDBNull(r.GetOrdinal("connection")) ? null : r.GetString(r.GetOrdinal("connection")),
            Port = r.IsDBNull(r.GetOrdinal("port")) ? null : r.GetString(r.GetOrdinal("port")),
            Session = r.IsDBNull(r.GetOrdinal("session")) ? null : r.GetString(r.GetOrdinal("session")),
            TranStatusText = r.IsDBNull(r.GetOrdinal("tran_status")) ? null : r.GetString(r.GetOrdinal("tran_status")),
            AppTypeName = r.IsDBNull(r.GetOrdinal("app_type")) ? null : r.GetString(r.GetOrdinal("app_type")),
            FileName = r.IsDBNull(r.GetOrdinal("file_name")) ? null : r.GetString(r.GetOrdinal("file_name")),
            FileSize = r.GetInt64(r.GetOrdinal("file_size")),
            FileSizeFormatted = r.IsDBNull(r.GetOrdinal("file_size_formatted")) ? null : r.GetString(r.GetOrdinal("file_size_formatted"))
        };
    }

    private static TechLogDoc MapTechLogDoc(SqliteDataReader r)
    {
        var dateStr = r.GetString(r.GetOrdinal("date"));
        DateTime.TryParseExact(dateStr, "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt);

        Dictionary<string, string> props = [];
        if (!r.IsDBNull(r.GetOrdinal("props")))
        {
            var propsJson = r.GetString(r.GetOrdinal("props"));
            try
            {
                props = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(propsJson) ?? [];
            }
            catch { }
        }

        return new TechLogDoc
        {
            Id = r.GetString(r.GetOrdinal("id")),
            Date = dt,
            DateFormatted = r.IsDBNull(r.GetOrdinal("date_formatted")) ? dateStr : r.GetString(r.GetOrdinal("date_formatted")),
            Duration = r.GetInt64(r.GetOrdinal("duration")),
            DurationMs = r.GetDouble(r.GetOrdinal("duration_ms")),
            DurationSec = r.GetDouble(r.GetOrdinal("duration_sec")),
            DurationFormatted = r.GetString(r.GetOrdinal("duration_formatted")),
            Event = r.GetString(r.GetOrdinal("event")),
            Level = r.GetInt32(r.GetOrdinal("level")),
            ProcessName = r.IsDBNull(r.GetOrdinal("process_name")) ? null : r.GetString(r.GetOrdinal("process_name")),
            ProcessId = r.IsDBNull(r.GetOrdinal("process_id")) ? null : r.GetString(r.GetOrdinal("process_id")),
            Spid = r.IsDBNull(r.GetOrdinal("spid")) ? null : r.GetString(r.GetOrdinal("spid")),
            OSThread = r.IsDBNull(r.GetOrdinal("os_thread")) ? null : r.GetString(r.GetOrdinal("os_thread")),
            SessionId = r.IsDBNull(r.GetOrdinal("session_id")) ? null : r.GetString(r.GetOrdinal("session_id")),
            LongInfoName = r.IsDBNull(r.GetOrdinal("long_info_name")) ? null : r.GetString(r.GetOrdinal("long_info_name")),
            LongInfoWait = r.IsDBNull(r.GetOrdinal("long_info_wait")) ? null : r.GetInt64(r.GetOrdinal("long_info_wait")),
            User = r.IsDBNull(r.GetOrdinal("user")) ? null : r.GetString(r.GetOrdinal("user")),
            App = r.IsDBNull(r.GetOrdinal("app")) ? null : r.GetString(r.GetOrdinal("app")),
            ConnectId = r.IsDBNull(r.GetOrdinal("connect_id")) ? null : r.GetString(r.GetOrdinal("connect_id")),
            ClientId = r.IsDBNull(r.GetOrdinal("client_id")) ? null : r.GetString(r.GetOrdinal("client_id")),
            Sql = r.IsDBNull(r.GetOrdinal("sql")) ? null : r.GetString(r.GetOrdinal("sql")),
            Rows = r.IsDBNull(r.GetOrdinal("rows")) ? null : r.GetInt64(r.GetOrdinal("rows")),
            Context = r.IsDBNull(r.GetOrdinal("context")) ? null : r.GetString(r.GetOrdinal("context")),
            Locks = r.IsDBNull(r.GetOrdinal("locks")) ? null : r.GetString(r.GetOrdinal("locks")),
            WaitConnections = r.IsDBNull(r.GetOrdinal("wait_connections")) ? null : r.GetString(r.GetOrdinal("wait_connections")),
            LkSrc = r.IsDBNull(r.GetOrdinal("lksrc")) ? null : r.GetString(r.GetOrdinal("lksrc")),
            Descr = r.IsDBNull(r.GetOrdinal("descr")) ? null : r.GetString(r.GetOrdinal("descr")),
            InBytes = r.IsDBNull(r.GetOrdinal("in_bytes")) ? null : r.GetInt64(r.GetOrdinal("in_bytes")),
            OutBytes = r.IsDBNull(r.GetOrdinal("out_bytes")) ? null : r.GetInt64(r.GetOrdinal("out_bytes")),
            Method = r.IsDBNull(r.GetOrdinal("method")) ? null : r.GetString(r.GetOrdinal("method")),
            Url = r.IsDBNull(r.GetOrdinal("url")) ? null : r.GetString(r.GetOrdinal("url")),
            Properties = props
        };
    }

    /// <summary>
    /// Мгновенное вычисление суммарного размера файлов кэша на диске (.db, .db-wal, .db-shm).
    /// </summary>
    public long GetCacheFileSizeBytes()
    {
        long total = 0;
        try
        {
            if (File.Exists(_dbFilePath))
                total += new FileInfo(_dbFilePath).Length;

            var walPath = _dbFilePath + "-wal";
            if (File.Exists(walPath))
                total += new FileInfo(walPath).Length;

            var shmPath = _dbFilePath + "-shm";
            if (File.Exists(shmPath))
                total += new FileInfo(shmPath).Length;
        }
        catch { }

        return total;
    }

    /// <summary>
    /// Полное удаление временных файлов текущей сессии кэша.
    /// </summary>
    public void Cleanup()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _connection.Close();
            SqliteConnection.ClearPool(_connection);
            _connection.Dispose();
        }
        catch { }

        DeleteFileIfExists(_dbFilePath);
        DeleteFileIfExists(_dbFilePath + "-wal");
        DeleteFileIfExists(_dbFilePath + "-shm");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            SqliteConnection.ClearPool(_connection);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch { }

        DeleteFileIfExists(_dbFilePath);
        DeleteFileIfExists(_dbFilePath + "-wal");
        DeleteFileIfExists(_dbFilePath + "-shm");
    }

    public void Dispose() => Cleanup();

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// Очистка всех зависших или старых сессионных кэшей в папке temp (при старте и выходе).
    /// </summary>
    public static void CleanupAllOrphanedTempFiles(string? tempDir = null)
    {
        var dir = tempDir ?? GetDefaultTempDirectory();
        if (!Directory.Exists(dir)) return;

        try
        {
            var files = Directory.GetFiles(dir, "session_*.*");
            foreach (var f in files)
            {
                DeleteFileIfExists(f);
            }
        }
        catch { }
    }
}
