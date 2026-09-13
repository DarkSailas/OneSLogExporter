using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Services;
using OneSLogExporter.Core.State;

namespace OneSLogExporter.Service.Workers;

/// <summary>
/// Фоновый воркер регулярного мониторинга и инкрементального экспорта Журнала Регистрации 1С по таймеру.
/// Реализует двухэтапный конвейер (Stage 1 -> Stage 2):
/// 1) Быстрый сбор и локальный сброс в NDJSON (с мгновенным освобождением файлов 1С от блокировок).
/// 2) Транспортировка накопленных JSON-данных в ClickHouse / Elasticsearch через JsonLogTransporter.
/// </summary>
public sealed class EventLogWorker(
    IOptions<ExporterOptions> options,
    FileDumper fileDumper,
    JsonLogTransporter jsonLogTransporter,
    StateTracker stateTracker,
    ILogger<EventLogWorker> logger) : BackgroundService
{
    private readonly ExporterOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, (DateTime LastWriteTime, LgfDictionary Dictionary)> _dictCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _discoveredLogged = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Немедленно отдаем управление SCM хосту Windows, чтобы служба мгновенно рапортовала статус SERVICE_RUNNING
        await Task.Yield();

        if (!_options.EventLog.Enabled || string.IsNullOrWhiteSpace(_options.EventLog.DirectoryPath))
        {
            logger.LogInformation("Мониторинг Журнала Регистрации отключен (EventLog.Enabled = false или не указан DirectoryPath).");
            return;
        }

        var hasAnyEventLogConsumer = _options.FileDump.IsEventLogActive
            || _options.ClickHouse.IsEventLogActive
            || _options.Elastic.IsEventLogActive;

        if (!hasAnyEventLogConsumer)
        {
            logger.LogInformation("Журнал Регистрации: сбор включен (EventLog.Enabled=true), но ни один сервис не подписан на ЖР (EventLogEnabled=false). Сканирование каталога ЖР пропущено.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.FileDump.EventLogDirectoryPath))
        {
            logger.LogError("КРИТИЧЕСКАЯ ОШИБКА: Мониторинг Журнала Регистрации включен (EventLog.Enabled = true), но не задан обязательный каталог для выгрузки JSON-дампов (FileDump.EventLogDirectoryPath)! Экспорт ЖР остановлен.");
            return;
        }

        var intervalSec = Math.Max(1, _options.PollingIntervalSeconds);
        logger.LogInformation("Запущен регулярный таймер мониторинга Журнала Регистрации 1С. Интервал опроса: {Interval} сек. Режим: Инкрементальный (только новые данные). Обязательный JSON дамп: {DumpDir}",
            intervalSec, _options.FileDump.EventLogDirectoryPath);
        await stateTracker.LoadAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessEventLogsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в цикле обработки Журнала Регистрации 1С");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<LgfDictionary> GetOrCreateDictionaryAsync(string? dictPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(dictPath) || !File.Exists(dictPath))
            return new LgfDictionary();

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(dictPath);
            if (_dictCache.TryGetValue(dictPath, out var cached) && cached.LastWriteTime == lastWrite)
            {
                return cached.Dictionary;
            }

            logger.LogDebug("Парсинг словаря 1Cv8.lgf [{DictPath}]...", dictPath);
            var parsed = await EventLogParser.ParseDictionaryAsync(dictPath, ct).ConfigureAwait(false);
            _dictCache[dictPath] = (lastWrite, parsed);
            return parsed;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ошибка при чтении словаря 1Cv8.lgf из {DictPath}. Используется пустой словарь.", dictPath);
            return new LgfDictionary();
        }
    }

    /// <summary>
    /// Функция сканирования файлов Журнала Регистрации: этап 1 (1С -> JSON) и этап 2 (JSON -> Хранилища).
    /// </summary>
    private async ValueTask ProcessEventLogsAsync(CancellationToken ct)
    {
        var rootDir = _options.EventLog.DirectoryPath;
        if (string.IsNullOrWhiteSpace(rootDir))
            return;

        if (!Directory.Exists(rootDir) && !File.Exists(rootDir))
        {
            logger.LogWarning("Каталог/файл Журнала Регистрации не найден: {RootDir}", rootDir);
            return;
        }

        List<(string TargetDir, string? DictionaryPath)> targetScopes = [];

        if (!string.IsNullOrWhiteSpace(_options.EventLog.DatabaseName))
        {
            var matchedBases = LogDiscovery.ResolveInfobases(rootDir, _options.EventLog.DatabaseName);
            if (matchedBases.Count > 0)
            {
                foreach (var b in matchedBases)
                {
                    if (_discoveredLogged.Add(b.Guid))
                    {
                        logger.LogInformation("Обнаружена целевая база 1С '{Name}' [GUID: {Guid}] в {Path}. Словарь: {DictPath}",
                            b.Name, b.Guid, b.DirectoryPath, b.DictionaryPath ?? "не найден");
                    }
                    targetScopes.Add((b.DirectoryPath, b.DictionaryPath));
                }
            }
            else
            {
                var all = LogDiscovery.DiscoverInfobases(rootDir);
                if (all.Count > 0)
                {
                    var available = string.Join(", ", all.Select(a => $"'{a.Name}' ({a.Guid})"));
                    logger.LogWarning("База '{TargetName}' не найдена в реестре кластера 1С ({RootDir}). Доступные базы: {Available}",
                        _options.EventLog.DatabaseName, rootDir, available);
                    return;
                }
                else
                {
                    logger.LogWarning("База '{TargetName}' задана, но реестр кластера (1CV8Clst.lst) не обнаружен в {RootDir}. Выполняется прямое сканирование пути.",
                        _options.EventLog.DatabaseName, rootDir);
                    targetScopes.Add((rootDir, LogDiscovery.FindEventLogDictionary(rootDir)));
                }
            }
        }
        else
        {
            targetScopes.Add((rootDir, LogDiscovery.FindEventLogDictionary(rootDir)));
        }

        var fileNameFilter = _options.EventLog.FileName;

        // =========================================================================
        // ЭТАП 1: Сбор и парсинг 1С в локальный JSON с мгновенным закрытием файлов 1С
        // =========================================================================
        foreach (var (scopeDir, dictPath) in targetScopes)
        {
            if (ct.IsCancellationRequested) break;

            var dictionary = await GetOrCreateDictionaryAsync(dictPath ?? LogDiscovery.FindEventLogDictionary(scopeDir), ct).ConfigureAwait(false);

            var lgpFiles = LogDiscovery.FindEventLogFiles(scopeDir, fileNameFilter).ToList();
            if (lgpFiles.Count == 0 && File.Exists(scopeDir))
            {
                lgpFiles.Add(scopeDir);
            }

            if (lgpFiles.Count == 0)
            {
                logger.LogDebug("Файлы событий Журнала Регистрации (*.lgp, *.lgd) не найдены в {ScopeDir}", scopeDir);
                continue;
            }

            foreach (var targetFilePath in lgpFiles)
            {
                if (ct.IsCancellationRequested) break;

            try
            {
                var fileInfo = new FileInfo(targetFilePath);
                if (!fileInfo.Exists)
                {
                    logger.LogDebug("Файл ЖР {FilePath} не существует, пропускаем.", targetFilePath);
                    continue;
                }

                var isLgd = targetFilePath.EndsWith(".lgd", StringComparison.OrdinalIgnoreCase);
                var fileName = Path.GetFileName(targetFilePath);

                // Если файл еще не отслеживался и LoadArchive = false -> устанавливаем отсечку на текущий конец (live)
                if (!stateTracker.HasTrackedState(targetFilePath) && !_options.EventLog.LoadArchive)
                {
                    if (isLgd)
                    {
                        var maxRowId = await LgdParser.GetMaxRowIdAsync(targetFilePath, ct).ConfigureAwait(false);
                        stateTracker.MarkFilePosition(targetFilePath, maxRowId, fileInfo.Length);
                        await stateTracker.SaveAsync(ct).ConfigureAwait(false);
                        logger.LogInformation("База ЖР {FileName}: первичный запуск (LoadArchive=false). Установлена отсечка на текущий rowID {MaxRowId} (выгружаются только новые live-события).", fileName, maxRowId);
                        continue;
                    }
                    else
                    {
                        stateTracker.MarkFilePosition(targetFilePath, fileInfo.Length, fileInfo.Length);
                        await stateTracker.SaveAsync(ct).ConfigureAwait(false);
                        logger.LogInformation("Файл ЖР {FileName}: первичный запуск (LoadArchive=false). Установлена отсечка на конец файла {Length} байт (выгружаются только новые live-события).", fileName, fileInfo.Length);
                        continue;
                    }
                }

                if (!stateTracker.HasFileGrown(targetFilePath, fileInfo.Length))
                {
                    logger.LogDebug("Файл ЖР {FileName} без изменений, пропускаем.", fileName);
                    continue;
                }

                var lastPos = stateTracker.GetLastPosition(targetFilePath);
                List<EventLogDoc> newDocs;
                long newPos;

                if (isLgd)
                {
                    logger.LogInformation("Инкрементальная обработка базы ЖР SQLite {FileName} с rowID > {LastRowId}...", fileName, lastPos);
                    (newDocs, newPos) = await LgdParser.ParseLgdIncrementalAsync(targetFilePath, lastPos, _options.Elastic.BulkBatchSize, ct).ConfigureAwait(false);
                }
                else
                {
                    logger.LogInformation("Инкрементальная обработка файла Журнала Регистрации {FileName} со смещения {LastPos} байт...", fileName, lastPos);
                    (newDocs, newPos) = await EventLogParser.ParseLogFromOffsetAsync(targetFilePath, dictionary, lastPos, ct).ConfigureAwait(false);
                }

                // Записываем новые распарсенные записи в локальный обязательный JSON дамп
                if (newDocs.Count > 0)
                {
                    await fileDumper.DumpEventLogsAsync(_options.EventLog.IndexId, newDocs, ct).ConfigureAwait(false);
                    logger.LogInformation("Файл ЖР {FileName}: сохранено {Count} новых записей в локальный JSON дамп [{Unit} {OldPos} -> {NewPos}].",
                        fileName, newDocs.Count, isLgd ? "rowID" : "байт", lastPos, newPos);
                }

                stateTracker.MarkFilePosition(targetFilePath, newPos, fileInfo.Length);
                await stateTracker.SaveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
            {
                logger.LogWarning("Файл ЖР {FilePath} недоступен или удален во время обработки: {Message}", targetFilePath, ex.Message);
            }
        }
        }

        // =========================================================================
        // ЭТАП 2: Транспортировка из локального JSON дампа в ClickHouse и Elasticsearch
        // Все файлы 1С закрыты, сетевые задержки внешних БД не блокируют 1С!
        // =========================================================================
        await jsonLogTransporter.TransportEventLogsAsync(ct).ConfigureAwait(false);
    }
}