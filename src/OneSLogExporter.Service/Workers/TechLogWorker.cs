using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Services;
using OneSLogExporter.Core.State;

namespace OneSLogExporter.Service.Workers;

/// <summary>
/// Фоновый воркер регулярного мониторинга и инкрементального экспорта Технологического Журнала 1С по таймеру.
/// Реализует двухэтапный конвейер (Stage 1 -> Stage 2):
/// 1) Быстрый сбор и сброс логов 1С в NDJSON с мгновенным закрытием дескрипторов файлов.
/// 2) Транспортировка накопленных JSON-данных в ClickHouse / Elasticsearch через JsonLogTransporter.
/// </summary>
public sealed class TechLogWorker(
    IOptions<ExporterOptions> options,
    FileDumper fileDumper,
    JsonLogTransporter jsonLogTransporter,
    ClickHousePublisher clickHousePublisher,
    ElasticPublisher elasticPublisher,
    StateTracker stateTracker,
    ILogger<TechLogWorker> logger) : BackgroundService
{
    private readonly ExporterOptions _options = options.Value;
    private bool _isFirstScan = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Немедленно отдаем управление SCM хосту Windows, чтобы служба мгновенно рапортовала статус SERVICE_RUNNING
        await Task.Yield();

        if (!_options.TechLog.Enabled || string.IsNullOrWhiteSpace(_options.TechLog.DirectoryPath))
        {
            logger.LogInformation("Мониторинг Технологического Журнала отключен (TechLog.Enabled = false или не указан DirectoryPath).");
            return;
        }

        var hasAnyTechLogConsumer = _options.FileDump.IsTechLogActive
            || _options.ClickHouse.IsTechLogActive
            || _options.Elastic.IsTechLogActive;

        if (!hasAnyTechLogConsumer)
        {
            logger.LogInformation("Технологический Журнал: сбор включен (TechLog.Enabled=true), но ни один сервис не подписан на ТЖ (TechLogEnabled=false). Сканирование каталога ТЖ пропущено.");
            return;
        }

        if ((_options.FileDump.IsTechLogActive || !_options.TechLog.DirectStream) && string.IsNullOrWhiteSpace(_options.FileDump.TechLogDirectoryPath))
        {
            logger.LogError("КРИТИЧЕСКАЯ ОШИБКА: Мониторинг Технологического Журнала включен (TechLog.Enabled = true), но не задан обязательный каталог для выгрузки JSON-дампов (FileDump.TechLogDirectoryPath)! Экспорт ТЖ остановлен.");
            return;
        }

        var intervalSec = Math.Max(1, _options.PollingIntervalSeconds);
        logger.LogInformation("Запущен регулярный таймер мониторинга Технологического Журнала 1С. Интервал опроса: {Interval} сек. Режим: {Mode}. Каталог дампа: {DumpDir}",
            intervalSec, _options.TechLog.DirectStream ? "Direct-Stream (RAM -> DB)" : "TwoStage (Disk Dump -> DB)", _options.FileDump.TechLogDirectoryPath);
        await stateTracker.LoadAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessTechLogsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в цикле обработки Технологического Журнала 1С");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSec), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Функция сканирования файлов ТЖ: этап 1 (1С -> JSON) и этап 2 (JSON -> Хранилища).
    /// </summary>
    private async ValueTask ProcessTechLogsAsync(CancellationToken ct)
    {
        var rootDir = _options.TechLog.DirectoryPath;
        if (string.IsNullOrWhiteSpace(rootDir))
            return;

        if (!Directory.Exists(rootDir) && !File.Exists(rootDir))
        {
            logger.LogWarning("Каталог/файл Технологического Журнала не найден: {RootDir}", rootDir);
            return;
        }

        var logItems = LogDiscovery.FindTechLogFiles(rootDir, _options.TechLog.MaxAgeHours).ToList();
        if (logItems.Count == 0 && File.Exists(rootDir))
        {
            var (pName, pId) = LogDiscovery.ParseProcessInfo(rootDir);
            logItems.Add((rootDir, pName, pId, Path.GetFileName(Path.GetDirectoryName(rootDir) ?? "default") ?? "default"));
        }

        if (logItems.Count == 0)
        {
            logger.LogDebug("Файлы Технологического Журнала (*.log) не найдены в {RootDir}", rootDir);
            return;
        }

        var initialSkippedCount = 0;

        // =========================================================================
        // ЭТАП 1: Сбор и парсинг ТЖ 1С в локальный JSON с мгновенным закрытием файлов
        // =========================================================================
        foreach (var (filePath, processName, processId, folderName) in logItems)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (!fileInfo.Exists)
                {
                    logger.LogDebug("Файл ТЖ {FilePath} не существует (удален или ротирован 1С), пропускаем.", filePath);
                    continue;
                }

                var fileName = Path.GetFileNameWithoutExtension(filePath);
                if (fileName.Length < 8) continue;

                // Если файл еще не отслеживался
                if (!stateTracker.HasTrackedState(filePath))
                {
                    if (_isFirstScan)
                    {
                        if (!_options.TechLog.LoadArchive)
                        {
                            stateTracker.MarkFilePosition(filePath, fileInfo.Length, fileInfo.Length);
                            initialSkippedCount++;
                            logger.LogDebug("Файл ТЖ {FileName} ({ProcessName}_{ProcessId}): первичный запуск (LoadArchive=false). Отсечка на конец файла {Length} байт.",
                                Path.GetFileName(filePath), processName, processId, fileInfo.Length);
                            continue;
                        }
                    }
                    else
                    {
                        // Файл появился уже во время работы службы (ротация периода 1С или запуск нового рабочего процесса) -> читаем новый файл с 0 байт
                        logger.LogDebug("Файл ТЖ {FileName} ({ProcessName}_{ProcessId}): обнаружен новый ротированный файл ТЖ. Чтение с 0 байт.",
                            Path.GetFileName(filePath), processName, processId);
                        stateTracker.MarkFilePosition(filePath, 0, fileInfo.Length);
                    }
                }

                if (!stateTracker.HasFileGrown(filePath, fileInfo.Length))
                    continue;

                var lastPos = stateTracker.GetLastPosition(filePath);
                logger.LogDebug("Инкрементальный разбор файла ТЖ {FileName} (процесс {ProcessName}_{ProcessId}) со смещения {LastPos} байт (True Chunking)...", Path.GetFileName(filePath), processName, processId, lastPos);

                var totalSavedCount = 0;
                var effectiveBatchSize = _options.ClickHouse.BulkBatchSize > 0
                    ? _options.ClickHouse.BulkBatchSize
                    : (_options.Elastic.BulkBatchSize > 0 ? _options.Elastic.BulkBatchSize : 25000);

                var newPos = await TechLogParser.ParseFileFromOffsetChunkedAsync(
                    filePath,
                    processName,
                    processId,
                    lastPos,
                    async batch =>
                    {
                        if (batch.Count > 0)
                        {
                            await ProcessTechLogBatchAsync(folderName, batch, ct).ConfigureAwait(false);
                            totalSavedCount += batch.Count;
                        }
                    },
                    batchSize: effectiveBatchSize,
                    filterEmptyEvents: _options.TechLog.FilterEmptyEvents,
                    ct: ct).ConfigureAwait(false);

                // Записываем новые распарсенные записи в локальный обязательный JSON дамп
                if (totalSavedCount > 0)
                {
                    logger.LogInformation("Файл ТЖ {FileName} (в {FolderName}): успешно обработано {Count} новых записей [смещение {OldPos} -> {NewPos} байт]. Режим: {Mode}",
                        Path.GetFileName(filePath), folderName, totalSavedCount, lastPos, newPos, _options.TechLog.DirectStream ? "Direct-Stream" : "TwoStage");
                }

                stateTracker.MarkFilePosition(filePath, newPos, fileInfo.Length);
                await stateTracker.SaveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Файл ТЖ {FilePath} недоступен или временно заблокирован: {Message}", filePath, ex.Message);
            }
        }

        if (_isFirstScan)
        {
            if (initialSkippedCount > 0)
            {
                logger.LogInformation("Первичный запуск мониторинга ТЖ (LoadArchive=false): для {Count} существующих файлов ТЖ установлена отсечка на конец файлов (выгружаются только новые live-события).",
                    initialSkippedCount);
                await stateTracker.SaveAsync(ct).ConfigureAwait(false);
            }
            _isFirstScan = false;
        }

        // =========================================================================
        // ЭТАП 2: Финальная довыгрузка оставшихся накопленных дампов ТЖ в хранилища (только для режима TwoStage)
        // =========================================================================
        if (!_options.TechLog.DirectStream)
        {
            try
            {
                await jsonLogTransporter.TransportTechLogsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Предупреждение при финальной транспортировке дампа ТЖ в хранилища");
            }
        }

        // Периодический возврат оперативной памяти в ОС Windows и компактизация LOH
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
    }

    private async ValueTask ProcessTechLogBatchAsync(string folderName, IReadOnlyList<TechLogDoc> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        if (_options.TechLog.DirectStream)
        {
            // Прямая потоковая отправка пакета напрямую из памяти в ClickHouse (до 20 000+ строк/сек)
            if (_options.ClickHouse.IsTechLogActive)
            {
                await clickHousePublisher.BulkInsertTechLogAsync(batch, ct).ConfigureAwait(false);
            }

            // Прямая потоковая отправка пакета напрямую из памяти в Elasticsearch / OpenSearch
            if (_options.Elastic.IsTechLogActive)
            {
                var indexName = IndexNamingHelper.BuildIndexName(
                    _options.Elastic.TechLogIndexPrefix,
                    _options.TechLog.IndexId,
                    _options.Elastic.Separation,
                    batch[0].Date);
                await elasticPublisher.BulkIndexTechLogAsync(indexName, batch, ct).ConfigureAwait(false);
            }

            // Параллельный / асинхронный сброс в локальный JSON-дамп на диске (только если FileDump включен)
            if (_options.FileDump.IsTechLogActive)
            {
                await fileDumper.DumpTechLogsAsync(folderName, batch, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // Двухэтапная классическая схема (TwoStage): сначала запись дампа на диск, затем чтение и транспортировка
            await fileDumper.DumpTechLogsAsync(folderName, batch, ct).ConfigureAwait(false);

            try
            {
                await jsonLogTransporter.TransportTechLogsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Предупреждение при потоковой транспортировке дампа ТЖ в хранилища");
            }
        }
    }
}