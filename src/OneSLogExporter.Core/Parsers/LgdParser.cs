using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using OneSLogExporter.Core.Models;

namespace OneSLogExporter.Core.Parsers;

/// <summary>
/// Высокопроизводительный потоковый парсер журнала регистрации 1С нового формата SQLite (1Cv8.lgd).
/// Обеспечивает безопасное неблокирующее чтение (ReadOnly/Shared Cache) баз данных размером 100+ ГБ.
/// </summary>
public static class LgdParser
{
    /// <summary>
    /// Словарь сопоставления системных наименований событий 1С с русскоязычными синонимами.
    /// </summary>
    private static readonly Dictionary<string, string> SystemEventAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_$Session$_.Start"] = "Сеанс. Начало",
        ["_$Session$_.Finish"] = "Сеанс. Завершение",
        ["_$Session$_.Authentication"] = "Сеанс. Аутентификация",
        ["_$InfoBase$_.ConfigUpdate"] = "Информационная база. Изменение конфигурации",
        ["_$InfoBase$_.DBConfigUpdate"] = "Информационная база. Изменение конфигурации базы данных",
        ["_$InfoBase$_.EventLogSettingsUpdate"] = "Информационная база. Изменение параметров журнала регистрации",
        ["_$InfoBase$_.InfoBaseAdmParamsUpdate"] = "Информационная база. Изменение параметров информационной базы",
        ["_$InfoBase$_.MasterNodeUpdate"] = "Информационная база. Изменение главного узла",
        ["_$InfoBase$_.RegionalSettingsUpdate"] = "Информационная база. Изменение региональных установок",
        ["_$InfoBase$_.TARInfo"] = "Тестирование и исправление. Сообщение",
        ["_$InfoBase$_.TARMess"] = "Тестирование и исправление. Предупреждение",
        ["_$InfoBase$_.TARImportant"] = "Тестирование и исправление. Ошибка",
        ["_$Data$_.New"] = "Данные. Добавление",
        ["_$Data$_.Update"] = "Данные. Изменение",
        ["_$Data$_.Delete"] = "Данные. Удаление",
        ["_$Data$_.TotalsPeriodUpdate"] = "Данные. Изменение периода рассчитанных итогов",
        ["_$Data$_.Post"] = "Данные. Проведение",
        ["_$Data$_.Unpost"] = "Данные. Отмена проведения",
        ["_$User$_.New"] = "Пользователи. Добавление",
        ["_$User$_.Update"] = "Пользователи. Изменение",
        ["_$User$_.Delete"] = "Пользователи. Удаление",
        ["_$Job$_.Start"] = "Фоновое задание. Запуск",
        ["_$Job$_.Succeed"] = "Фоновое задание. Успешное завершение",
        ["_$Job$_.Fail"] = "Фоновое задание. Ошибка выполнения",
        ["_$Job$_.Cancel"] = "Фоновое задание. Отмена",
        ["_$PerformError$_"] = "Ошибка выполнения",
        ["_$Transaction$_.Begin"] = "Транзакция. Начало",
        ["_$Transaction$_.Commit"] = "Транзакция. Фиксация",
        ["_$Transaction$_.Rollback"] = "Транзакция. Отмена"
    };

    /// <summary>
    /// Потоковый разбор базы данных 1Cv8.lgd (SQLite) с поддержкой лимита и прогресса.
    /// </summary>
    public static async IAsyncEnumerable<EventLogDoc> ParseLgdAsync(
        string lgdFilePath,
        int maxRecords = int.MaxValue,
        IProgress<(long Processed, long Total)>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        DateTime? filterDateFrom = null,
        DateTime? filterDateTo = null)
    {
        if (!File.Exists(lgdFilePath))
            yield break;

        var connStr = BuildConnectionString(lgdFilePath);
        await using var connection = new SqliteConnection(connStr);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, ct).ConfigureAwait(false);

        // 1. Проверяем наличие таблицы EventLog
        var hasEventLog = false;
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EventLog';";
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            hasEventLog = count > 0;
        }

        if (!hasEventLog)
            yield break;

        // 2. Загружаем словари кодов в оперативную память
        var (users, userUuids) = await LoadUserCodesAsync(connection, ct).ConfigureAwait(false);
        var computers = await LoadLookupAsync(connection, "ComputerCodes", ct).ConfigureAwait(false);
        var apps = await LoadLookupAsync(connection, "AppCodes", ct).ConfigureAwait(false);
        var events = await LoadLookupAsync(connection, "EventCodes", ct).ConfigureAwait(false);
        var metadata = await LoadLookupAsync(connection, "MetadataCodes", ct).ConfigureAwait(false);
        var servers = await LoadLookupAsync(connection, "WorkServerCodes", ct).ConfigureAwait(false);
        var ports = await LoadLookupAsync(connection, "PrimaryPortCodes", ct).ConfigureAwait(false);

        // 3. Определяем структуру колонок EventLog для адаптивности к разным релизам 1С
        var availableCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var colCmd = connection.CreateCommand())
        {
            colCmd.CommandText = "PRAGMA table_info(EventLog);";
            await using var colReader = await colCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await colReader.ReadAsync(ct).ConfigureAwait(false))
            {
                availableCols.Add(colReader.GetString(1));
            }
        }

        var hasComputer = availableCols.Contains("computerCode");
        var hasServer = availableCols.Contains("workServerCode");
        var hasPort = availableCols.Contains("primaryPortCode");

        // 4. Определяем общее количество записей для шкалы прогресса
        long totalCount = 0;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT MAX(rowID) FROM EventLog;";
            var maxIdObj = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (maxIdObj != null && maxIdObj != DBNull.Value && long.TryParse(maxIdObj.ToString(), out var maxRowId))
            {
                totalCount = maxRowId;
            }
        }

        if (totalCount <= 0) totalCount = 1;

        var limitCount = maxRecords < int.MaxValue && maxRecords > 0 ? maxRecords : totalCount;
        var progressTarget = Math.Min(limitCount, totalCount);

        var fileInfo = new FileInfo(lgdFilePath);
        var fileName = Path.GetFileName(lgdFilePath);
        var fileSize = fileInfo.Exists ? fileInfo.Length : 0L;
        var fileSizeFormatted = FormatFileSize(fileSize);

        // 5. Потоковое чтение записей в обратном хронологическом порядке (сначала новые)
        var compCol = hasComputer ? "computerCode" : "0";
        var serverCol = hasServer ? "workServerCode" : "0";
        var portCol = hasPort ? "primaryPortCode" : "0";

        await using var queryCmd = connection.CreateCommand();

        var whereClauses = new List<string>(2);
        if (filterDateFrom.HasValue)
        {
            whereClauses.Add("date >= @minDateVal");
            queryCmd.Parameters.AddWithValue("@minDateVal", filterDateFrom.Value.Date.Ticks / 1000);
        }
        if (filterDateTo.HasValue)
        {
            whereClauses.Add("date <= @maxDateVal");
            queryCmd.Parameters.AddWithValue("@maxDateVal", filterDateTo.Value.Date.AddDays(1).AddTicks(-1).Ticks / 1000);
        }

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

        queryCmd.CommandText = maxRecords < int.MaxValue && maxRecords > 0
            ? $"SELECT rowID, severity, date, connectID, session, transactionStatus, transactionID, userCode, appCode, eventCode, comment, dataPresentation, metadataCodes, {compCol}, {serverCol}, {portCol} FROM EventLog {whereSql} ORDER BY rowID DESC LIMIT @limit;"
            : $"SELECT rowID, severity, date, connectID, session, transactionStatus, transactionID, userCode, appCode, eventCode, comment, dataPresentation, metadataCodes, {compCol}, {serverCol}, {portCol} FROM EventLog {whereSql} ORDER BY rowID DESC;";

        if (maxRecords < int.MaxValue && maxRecords > 0)
        {
            queryCmd.Parameters.AddWithValue("@limit", maxRecords);
        }

        await using var reader = await queryCmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);

        long processed = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested)
                yield break;

            processed++;
            if (processed % 250 == 0 || processed == progressTarget)
            {
                progress?.Report((processed, progressTarget));
            }

            var rowId = reader.GetInt64(0);
            var severityVal = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var dateVal = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            var connectId = reader.IsDBNull(3) ? string.Empty : reader.GetInt64(3).ToString();
            var session = reader.IsDBNull(4) ? string.Empty : reader.GetInt64(4).ToString();
            var tranStatus = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var tranId = reader.IsDBNull(6) ? string.Empty : reader.GetInt64(6).ToString();

            var userCode = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
            var appCode = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
            var eventCode = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
            var comment = reader.IsDBNull(10) ? string.Empty : EventLogParser.SanitizeText(reader.GetString(10));
            var dataPresentation = reader.IsDBNull(11) ? string.Empty : EventLogParser.SanitizeText(reader.GetString(11));
            var metadataCodeRaw = reader.IsDBNull(12) ? string.Empty : reader.GetString(12);

            var computerCode = reader.IsDBNull(13) ? 0 : reader.GetInt32(13);
            var serverCode = reader.IsDBNull(14) ? 0 : reader.GetInt32(14);
            var portCode = reader.IsDBNull(15) ? 0 : reader.GetInt32(15);

            // Дата: количество десятков микросекунд (1/10000 сек) с 0001-01-01
            DateTime parsedDate;
            try
            {
                parsedDate = dateVal > 0 ? new DateTime(dateVal * 1000, DateTimeKind.Utc) : DateTime.UtcNow;
            }
            catch
            {
                parsedDate = DateTime.UtcNow;
            }

            var minDate = filterDateFrom?.Date ?? DateTime.MinValue;
            var maxDate = filterDateTo.HasValue ? filterDateTo.Value.Date.AddDays(1).AddTicks(-1) : DateTime.MaxValue;

            if (filterDateFrom.HasValue && parsedDate < minDate)
            {
                // Записи считываются по убыванию rowID (от новых к старым): более ранние записи пропускаются целиком
                yield break;
            }
            if (filterDateTo.HasValue && parsedDate > maxDate) continue;

            var importance = severityVal switch
            {
                0 => "Информация",
                1 => "Предупреждение",
                2 => "Ошибка",
                3 => "Примечание",
                _ => $"Уровень {severityVal}"
            };

            var tranStatusStr = tranStatus switch
            {
                0 => "-",
                1 => "X",
                2 => "R",
                3 => "InProc",
                _ => tranStatus.ToString()
            };

            var tranStatusText = tranStatus switch
            {
                0 => "Вне транзакции",
                1 => "Отменена",
                2 => "Зафиксирована",
                3 => "В процессе",
                _ => tranStatusStr
            };

            var tranFull = string.IsNullOrEmpty(tranId) ? tranStatusStr : $"{tranStatusStr}({tranId})";

            var user = users.TryGetValue(userCode, out var u)
                ? (!string.IsNullOrEmpty(u) ? u : (userCode == 1 ? "<Не указан>" : $"User #{userCode}"))
                : (userCode == 1 ? "<Не указан>" : (userCode > 0 ? $"User #{userCode}" : string.Empty));
            var userUuid = userUuids.TryGetValue(userCode, out var uid) ? uid : string.Empty;
            var computer = computers.TryGetValue(computerCode, out var c) ? c : (computerCode > 0 ? $"Comp #{computerCode}" : string.Empty);
            var server = servers.TryGetValue(serverCode, out var srv) ? srv : (serverCode > 0 ? $"Server #{serverCode}" : string.Empty);
            var port = ports.TryGetValue(portCode, out var p) ? p : (portCode > 0 ? portCode.ToString() : string.Empty);
            var rawEventName = events.TryGetValue(eventCode, out var e) ? e : (eventCode > 0 ? $"Event #{eventCode}" : string.Empty);
            var eventName = !string.IsNullOrEmpty(rawEventName) && EventLogParser.SystemEventAliases.TryGetValue(rawEventName, out var alias)
                ? alias
                : rawEventName;

            user = FastStringPool.Intern(EventLogParser.SanitizeText(user));
            computer = FastStringPool.Intern(EventLogParser.SanitizeText(computer));
            server = FastStringPool.Intern(EventLogParser.SanitizeText(server));
            port = FastStringPool.Intern(port);
            eventName = FastStringPool.Intern(EventLogParser.SanitizeText(eventName));
            importance = FastStringPool.Intern(importance);
            tranStatusText = FastStringPool.Intern(tranStatusText);

            var app = apps.TryGetValue(appCode, out var a) ? a : (appCode > 0 ? $"App #{appCode}" : string.Empty);
            var appTypeName = app switch
            {
                "1CV8C" => "Тонкий клиент",
                "1CV8" => "Толстый клиент",
                "BackgroundJob" => "Фоновое задание",
                "WebClient" => "Веб-клиент",
                "COMConnector" => "COM-соединение",
                "WSConnection" => "Web-сервис",
                "HTTPServiceConnection" => "HTTP-сервис",
                "Designer" => "Конфигуратор",
                "RAS" => "Сервер администрирования (RAS)",
                "RAC" => "Консоль администрирования (RAC)",
                "WebServerExtension" => "Расширение веб-сервера",
                "OData" => "Интерфейс OData",
                "MobileClient" => "Мобильный клиент",
                "MobileServer" => "Мобильный сервер",
                "System" => "Системный процесс",
                _ => app
            };

            // Метаданные
            var meta = string.Empty;
            if (!string.IsNullOrEmpty(metadataCodeRaw))
            {
                if (int.TryParse(metadataCodeRaw, out var mCode))
                {
                    meta = metadata.TryGetValue(mCode, out var mName) ? mName : $"Meta #{mCode}";
                }
                else
                {
                    var parts = metadataCodeRaw.Split(new[] { ' ', ',', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        var resolved = new List<string>(parts.Length);
                        foreach (var part in parts)
                        {
                            if (int.TryParse(part, out var pCode) && metadata.TryGetValue(pCode, out var pName))
                            {
                                resolved.Add(pName);
                            }
                            else
                            {
                                resolved.Add($"Meta #{part}");
                            }
                        }
                        meta = string.Join(", ", resolved);
                    }
                    else
                    {
                        meta = metadataCodeRaw;
                    }
                }
            }

            var dateFormatted = parsedDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var docId = $"lgd_{rowId}_{dateVal}";

            yield return new EventLogDoc
            {
                Id = docId,
                Date = parsedDate,
                DateFormatted = dateFormatted,
                Event = eventName,
                User = user,
                UserUuid = userUuid,
                Meta = meta,
                MetadataUuid = string.Empty,
                Tran = tranFull,
                TransactionDate = tranStatus != 0 ? parsedDate : null,
                TransactionNumber = long.TryParse(tranId, out var tid) ? tid : 0L,
                TranStatusText = tranStatusText,
                App = app,
                AppTypeName = appTypeName,
                Comment = comment,
                Importance = importance,
                Data = dataPresentation,
                DataPresentation = dataPresentation,
                Computer = computer,
                Server = server,
                Connection = connectId,
                Port = port,
                AddPort = string.Empty,
                Session = session,
                FileName = fileName,
                FileSize = fileSize,
                FileSizeFormatted = fileSizeFormatted
            };
        }

        progress?.Report((progressTarget, progressTarget));
        }
        finally
        {
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// Получение максимального rowID в таблице EventLog файла базы SQLite (1Cv8.lgd).
    /// </summary>
    public static async ValueTask<long> GetMaxRowIdAsync(string lgdFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(lgdFilePath)) return 0;

        var connStr = BuildConnectionString(lgdFilePath);
        await using var connection = new SqliteConnection(connStr);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, ct).ConfigureAwait(false);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(rowID), 0) FROM EventLog;";
            var res = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return res != null && long.TryParse(res.ToString(), out var maxId) ? maxId : 0;
        }
        finally
        {
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// Инкрементальное чтение пачки новых записей из базы SQLite (1Cv8.lgd) с отслеживанием rowID в порядке возрастания (ASC).
    /// </summary>
    public static async Task<(List<EventLogDoc> Docs, long NewMaxRowId)> ParseLgdIncrementalAsync(
        string lgdFilePath,
        long fromRowId,
        int batchSize = 25000,
        bool filterEmptyTransactions = true,
        CancellationToken ct = default)
    {
        if (!File.Exists(lgdFilePath)) return ([], fromRowId);

        var connStr = BuildConnectionString(lgdFilePath);
        await using var connection = new SqliteConnection(connStr);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, ct).ConfigureAwait(false);

        // 1. Проверяем наличие таблицы EventLog
        await using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='EventLog';";
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count == 0) return ([], fromRowId);
        }

        // 2. Загружаем словари кодов
        var (users, userUuids) = await LoadUserCodesAsync(connection, ct).ConfigureAwait(false);
        var computers = await LoadLookupAsync(connection, "ComputerCodes", ct).ConfigureAwait(false);
        var apps = await LoadLookupAsync(connection, "AppCodes", ct).ConfigureAwait(false);
        var events = await LoadLookupAsync(connection, "EventCodes", ct).ConfigureAwait(false);
        var metadata = await LoadLookupAsync(connection, "MetadataCodes", ct).ConfigureAwait(false);
        var servers = await LoadLookupAsync(connection, "WorkServerCodes", ct).ConfigureAwait(false);
        var ports = await LoadLookupAsync(connection, "PrimaryPortCodes", ct).ConfigureAwait(false);

        // 3. Доступные колонки
        var availableCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var colCmd = connection.CreateCommand())
        {
            colCmd.CommandText = "PRAGMA table_info(EventLog);";
            await using var colReader = await colCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await colReader.ReadAsync(ct).ConfigureAwait(false))
            {
                availableCols.Add(colReader.GetString(1));
            }
        }

        var compCol = availableCols.Contains("computerCode") ? "computerCode" : "0";
        var serverCol = availableCols.Contains("workServerCode") ? "workServerCode" : "0";
        var portCol = availableCols.Contains("primaryPortCode") ? "primaryPortCode" : "0";

        var fileInfo = new FileInfo(lgdFilePath);
        var fileName = fileInfo.Name;
        var fileSize = fileInfo.Exists ? fileInfo.Length : 0L;
        var fileSizeFormatted = FormatFileSize(fileSize);

        await using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = $"SELECT rowID, severity, date, connectID, session, transactionStatus, transactionID, userCode, appCode, eventCode, comment, dataPresentation, metadataCodes, {compCol}, {serverCol}, {portCol} FROM EventLog WHERE rowID > @fromRowId ORDER BY rowID ASC LIMIT @limit;";
        queryCmd.Parameters.AddWithValue("@fromRowId", fromRowId);
        queryCmd.Parameters.AddWithValue("@limit", batchSize > 0 ? batchSize : 5000);

        await using var reader = await queryCmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);

        var docs = new List<EventLogDoc>(batchSize > 0 ? Math.Min(batchSize, 5000) : 1000);
        var maxObservedRowId = fromRowId;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested) break;

            var rowId = reader.GetInt64(0);
            if (rowId > maxObservedRowId) maxObservedRowId = rowId;

            var severityVal = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var dateVal = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            var connectId = reader.IsDBNull(3) ? string.Empty : reader.GetInt64(3).ToString();
            var session = reader.IsDBNull(4) ? string.Empty : reader.GetInt64(4).ToString();
            var tranStatus = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var tranId = reader.IsDBNull(6) ? string.Empty : reader.GetInt64(6).ToString();

            var userCode = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);
            var appCode = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
            var eventCode = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
            var comment = reader.IsDBNull(10) ? string.Empty : EventLogParser.SanitizeText(reader.GetString(10));
            var dataPresentation = reader.IsDBNull(11) ? string.Empty : EventLogParser.SanitizeText(reader.GetString(11));
            var metadataCodeRaw = reader.IsDBNull(12) ? string.Empty : reader.GetString(12);

            var computerCode = reader.IsDBNull(13) ? 0 : reader.GetInt32(13);
            var serverCode = reader.IsDBNull(14) ? 0 : reader.GetInt32(14);
            var portCode = reader.IsDBNull(15) ? 0 : reader.GetInt32(15);

            DateTime parsedDate;
            try
            {
                parsedDate = dateVal > 0 ? new DateTime(dateVal * 1000, DateTimeKind.Utc) : DateTime.UtcNow;
            }
            catch
            {
                parsedDate = DateTime.UtcNow;
            }

            var importance = severityVal switch
            {
                0 => "Информация",
                1 => "Предупреждение",
                2 => "Ошибка",
                3 => "Примечание",
                _ => $"Уровень {severityVal}"
            };

            var tranStatusStr = tranStatus switch
            {
                0 => "-",
                1 => "X",
                2 => "R",
                3 => "InProc",
                _ => tranStatus.ToString()
            };

            var tranStatusText = tranStatus switch
            {
                0 => "Вне транзакции",
                1 => "Отменена",
                2 => "Зафиксирована",
                3 => "В процессе",
                _ => tranStatusStr
            };

            var tranFull = string.IsNullOrEmpty(tranId) ? tranStatusStr : $"{tranStatusStr}({tranId})";

            var user = users.TryGetValue(userCode, out var u)
                ? (!string.IsNullOrEmpty(u) ? u : (userCode == 1 ? "<Не указан>" : $"User #{userCode}"))
                : (userCode == 1 ? "<Не указан>" : (userCode > 0 ? $"User #{userCode}" : string.Empty));
            var userUuid = userUuids.TryGetValue(userCode, out var uid) ? uid : string.Empty;
            var computer = computers.TryGetValue(computerCode, out var c) ? c : (computerCode > 0 ? $"Comp #{computerCode}" : string.Empty);
            var server = servers.TryGetValue(serverCode, out var srv) ? srv : (serverCode > 0 ? $"Server #{serverCode}" : string.Empty);
            var port = ports.TryGetValue(portCode, out var p) ? p : (portCode > 0 ? portCode.ToString() : string.Empty);
            var rawEventName = events.TryGetValue(eventCode, out var e) ? e : (eventCode > 0 ? $"Event #{eventCode}" : string.Empty);
            var eventName = !string.IsNullOrEmpty(rawEventName) && EventLogParser.SystemEventAliases.TryGetValue(rawEventName, out var alias)
                ? alias
                : rawEventName;

            if (filterEmptyTransactions && (eventName is "Транзакция. Начало" or "Транзакция. Фиксация" or "_$Transaction$_.Begin" or "_$Transaction$_.Commit"))
            {
                continue;
            }

            user = FastStringPool.Intern(EventLogParser.SanitizeText(user));
            computer = FastStringPool.Intern(EventLogParser.SanitizeText(computer));
            server = FastStringPool.Intern(EventLogParser.SanitizeText(server));
            port = FastStringPool.Intern(port);
            eventName = FastStringPool.Intern(EventLogParser.SanitizeText(eventName));
            importance = FastStringPool.Intern(importance);
            tranStatusText = FastStringPool.Intern(tranStatusText);

            var app = apps.TryGetValue(appCode, out var a) ? a : (appCode > 0 ? $"App #{appCode}" : string.Empty);
            var appTypeName = app switch
            {
                "1CV8C" => "Тонкий клиент",
                "1CV8" => "Толстый клиент",
                "BackgroundJob" => "Фоновое задание",
                "WebClient" => "Веб-клиент",
                "COMConnector" => "COM-соединение",
                "WSConnection" => "Web-сервис",
                "HTTPServiceConnection" => "HTTP-сервис",
                "Designer" => "Конфигуратор",
                "RAS" => "Сервер администрирования (RAS)",
                "RAC" => "Консоль администрирования (RAC)",
                "WebServerExtension" => "Расширение веб-сервера",
                "OData" => "Интерфейс OData",
                "MobileClient" => "Мобильный клиент",
                "MobileServer" => "Мобильный сервер",
                "System" => "Системный процесс",
                _ => app
            };

            var meta = string.Empty;
            if (!string.IsNullOrEmpty(metadataCodeRaw))
            {
                if (int.TryParse(metadataCodeRaw, out var mCode))
                {
                    meta = metadata.TryGetValue(mCode, out var mName) ? mName : $"Meta #{mCode}";
                }
                else
                {
                    var parts = metadataCodeRaw.Split(new[] { ' ', ',', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        var resolved = new List<string>(parts.Length);
                        foreach (var part in parts)
                        {
                            if (int.TryParse(part, out var pCode) && metadata.TryGetValue(pCode, out var pName))
                            {
                                resolved.Add(pName);
                            }
                            else
                            {
                                resolved.Add($"Meta #{part}");
                            }
                        }
                        meta = string.Join(", ", resolved);
                    }
                    else
                    {
                        meta = metadataCodeRaw;
                    }
                }
            }

            var dateFormatted = parsedDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var docId = $"lgd_{rowId}_{dateVal}";

            docs.Add(new EventLogDoc
            {
                Id = docId,
                Date = parsedDate,
                DateFormatted = dateFormatted,
                Event = eventName,
                User = user,
                UserUuid = userUuid,
                Meta = meta,
                MetadataUuid = string.Empty,
                Tran = tranFull,
                TransactionDate = tranStatus != 0 ? parsedDate : null,
                TransactionNumber = long.TryParse(tranId, out var tid) ? tid : 0L,
                TranStatusText = tranStatusText,
                App = app,
                AppTypeName = appTypeName,
                Comment = comment,
                Importance = importance,
                Data = dataPresentation,
                DataPresentation = dataPresentation,
                Computer = computer,
                Server = server,
                Connection = connectId,
                Port = port,
                AddPort = string.Empty,
                Session = session,
                FileName = fileName,
                FileSize = fileSize,
                FileSizeFormatted = fileSizeFormatted
            });
        }

        return (docs, maxObservedRowId);
        }
        finally
        {
            SqliteConnection.ClearPool(connection);
        }
    }

    private static async ValueTask<Dictionary<int, string>> LoadLookupAsync(SqliteConnection conn, string tableName, CancellationToken ct)
    {
        var dict = new Dictionary<int, string>();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count == 0) return dict;

            cmd.CommandText = $"SELECT code, name FROM {tableName};";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var code = reader.GetInt32(0);
                var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                dict[code] = name;
            }
        }
        catch { }
        return dict;
    }

    private static async ValueTask<(Dictionary<int, string> Users, Dictionary<int, string> UserUuids)> LoadUserCodesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var users = new Dictionary<int, string>();
        var userUuids = new Dictionary<int, string>();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='UserCodes';";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count == 0) return (users, userUuids);

            var hasUuid = false;
            cmd.CommandText = "PRAGMA table_info(UserCodes);";
            await using (var pragmaReader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await pragmaReader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (string.Equals(pragmaReader.GetString(1), "uuid", StringComparison.OrdinalIgnoreCase))
                    {
                        hasUuid = true;
                        break;
                    }
                }
            }

            cmd.CommandText = hasUuid
                ? "SELECT code, name, uuid FROM UserCodes;"
                : "SELECT code, name FROM UserCodes;";

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var code = reader.GetInt32(0);
                var name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                users[code] = name;

                if (hasUuid && !reader.IsDBNull(2))
                {
                    var uuidObj = reader.GetValue(2);
                    if (uuidObj is byte[] bytes && bytes.Length == 16)
                    {
                        userUuids[code] = new Guid(bytes).ToString();
                    }
                    else
                    {
                        var str = uuidObj?.ToString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(str))
                        {
                            userUuids[code] = str;
                        }
                    }
                }
            }
        }
        catch { }
        return (users, userUuids);
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} ГБ",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} МБ",
            >= 1024 => $"{bytes / 1024.0:F1} КБ",
            _ => $"{bytes} Б"
        };
    }
    private static string BuildConnectionString(string lgdFilePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = lgdFilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 3000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}