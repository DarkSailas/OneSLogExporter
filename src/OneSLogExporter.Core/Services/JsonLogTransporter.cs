using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Serialization;
using OneSLogExporter.Core.State;

namespace OneSLogExporter.Core.Services;

/// <summary>
/// Сервис второго этапа конвейера (Stage 2):
/// Выполняет транспортировку распарсенных данных из локальных файлов дампа JSON (NDJSON)
/// в целевые хранилища (ClickHouse и/или Elasticsearch / OpenSearch) с гарантией отслеживания позиции чтения в StateTracker.
/// Позволяет полностью изолировать файлы 1С от сетевых задержек внешних баз данных.
/// </summary>
public sealed class JsonLogTransporter
{
    private readonly ElasticPublisher _elasticPublisher;
    private readonly ClickHousePublisher _clickHousePublisher;
    private readonly StateTracker _stateTracker;
    private readonly ExporterOptions _options;
    private readonly ILogger<JsonLogTransporter> _logger;
    private readonly SemaphoreSlim _transportLock = new(1, 1);

    public JsonLogTransporter(
        ElasticPublisher elasticPublisher,
        ClickHousePublisher clickHousePublisher,
        StateTracker stateTracker,
        ExporterOptions options,
        ILogger<JsonLogTransporter> logger)
    {
        _elasticPublisher = elasticPublisher;
        _clickHousePublisher = clickHousePublisher;
        _stateTracker = stateTracker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Транспортировка накопленных записей Журнала Регистрации из локальных JSON-файлов в ClickHouse / Elastic.
    /// </summary>
    public async ValueTask TransportEventLogsAsync(CancellationToken ct = default)
    {
        var elasticEnabled = _options.Elastic.IsEventLogActive;
        var clickHouseEnabled = _options.ClickHouse.IsEventLogActive;
        if (!elasticEnabled && !clickHouseEnabled) return;

        var targetDir = _options.FileDump.EventLogDirectoryPath;
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir)) return;

        await _transportLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = Directory.EnumerateFiles(targetDir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => File.GetCreationTimeUtc(f))
                .ToList();

            if (files.Count == 0) return;

            var batchSize = Math.Max(1000, _options.ClickHouse.BulkBatchSize);

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists) continue;

                var lastPos = _stateTracker.GetLastPosition(file);
                if (fileInfo.Length <= lastPos) continue;

                var fileName = Path.GetFileName(file);
                var transportedCount = 0;

                await using var fs = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    65536,
                    useAsync: true);

                if (lastPos > 0 && lastPos < fs.Length)
                {
                    fs.Seek(lastPos, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 65536);

                var batch = new List<EventLogDoc>(batchSize);
                string? line;

                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    EventLogDoc? doc = null;
                    try
                    {
                        doc = JsonSerializer.Deserialize(line, LogJsonContext.Compact.EventLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка десериализации записи ЖР из файла {FileName}. Пропускаем строку.", fileName);
                    }

                    if (doc != null)
                    {
                        batch.Add(doc);
                    }

                    if (batch.Count >= batchSize)
                    {
                        await SendEventLogBatchAsync(batch, ct).ConfigureAwait(false);
                        transportedCount += batch.Count;
                        batch.Clear();

                        var currentPos = fs.Position;
                        _stateTracker.MarkFilePosition(file, currentPos, fileInfo.Length);
                        await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
                    }
                }

                if (batch.Count > 0 && !ct.IsCancellationRequested)
                {
                    await SendEventLogBatchAsync(batch, ct).ConfigureAwait(false);
                    transportedCount += batch.Count;
                    batch.Clear();

                    var currentPos = fs.Position;
                    _stateTracker.MarkFilePosition(file, currentPos, fileInfo.Length);
                    await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
                }

                if (transportedCount > 0)
                {
                    _logger.LogInformation("Транспорт ЖР: успешно передано {Count} записей из JSON {FileName} в хранилища.",
                        transportedCount, fileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой при транспортировке записей Журнала Регистрации из JSON-дампов");
        }
        finally
        {
            _transportLock.Release();
        }
    }

    /// <summary>
    /// Транспортировка накопленных записей Технологического Журнала из локальных JSON-файлов в ClickHouse / Elastic.
    /// </summary>
    public async ValueTask TransportTechLogsAsync(CancellationToken ct = default)
    {
        var elasticEnabled = _options.Elastic.IsTechLogActive;
        var clickHouseEnabled = _options.ClickHouse.IsTechLogActive;
        if (!elasticEnabled && !clickHouseEnabled) return;

        var targetDir = _options.FileDump.TechLogDirectoryPath;
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir)) return;

        await _transportLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var files = Directory.EnumerateFiles(targetDir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(f => File.GetCreationTimeUtc(f))
                .ToList();

            if (files.Count == 0) return;

            var batchSize = Math.Max(1000, _options.ClickHouse.BulkBatchSize);

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) break;

                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists) continue;

                var lastPos = _stateTracker.GetLastPosition(file);
                if (fileInfo.Length <= lastPos) continue;

                var fileName = Path.GetFileName(file);
                var transportedCount = 0;

                await using var fs = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    65536,
                    useAsync: true);

                if (lastPos > 0 && lastPos < fs.Length)
                {
                    fs.Seek(lastPos, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 65536);

                var batch = new List<TechLogDoc>(batchSize);
                string? line;

                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    TechLogDoc? doc = null;
                    try
                    {
                        doc = JsonSerializer.Deserialize(line, LogJsonContext.Compact.TechLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка десериализации записи ТЖ из файла {FileName}. Пропускаем строку.", fileName);
                    }

                    if (doc != null)
                    {
                        batch.Add(doc);
                    }

                    if (batch.Count >= batchSize)
                    {
                        await SendTechLogBatchAsync(batch, ct).ConfigureAwait(false);
                        transportedCount += batch.Count;
                        batch.Clear();

                        var currentPos = fs.Position;
                        _stateTracker.MarkFilePosition(file, currentPos, fileInfo.Length);
                        await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
                    }
                }

                if (batch.Count > 0 && !ct.IsCancellationRequested)
                {
                    await SendTechLogBatchAsync(batch, ct).ConfigureAwait(false);
                    transportedCount += batch.Count;
                    batch.Clear();

                    var currentPos = fs.Position;
                    _stateTracker.MarkFilePosition(file, currentPos, fileInfo.Length);
                    await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
                }

                if (transportedCount > 0)
                {
                    _logger.LogInformation("Транспорт ТЖ: успешно передано {Count} записей из JSON {FileName} в хранилища.",
                        transportedCount, fileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сбой при транспортировке записей Технологического Журнала из JSON-дампов");
        }
        finally
        {
            _transportLock.Release();
        }
    }

    private async ValueTask SendEventLogBatchAsync(List<EventLogDoc> batch, CancellationToken ct)
    {
        if (_options.Elastic.IsEventLogActive)
        {
            var separation = !string.IsNullOrWhiteSpace(_options.EventLog.Separation)
                ? _options.EventLog.Separation
                : _options.Elastic.Separation;

            foreach (var group in batch.GroupBy(d => IndexNamingHelper.BuildIndexName(_options.Elastic.EventLogIndexPrefix, _options.EventLog.IndexId, separation, d.Date)))
            {
                await _elasticPublisher.BulkIndexEventLogAsync(group.Key, group, ct).ConfigureAwait(false);
            }
        }

        if (_options.ClickHouse.IsEventLogActive)
        {
            await _clickHousePublisher.BulkInsertEventLogAsync(batch, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask SendTechLogBatchAsync(List<TechLogDoc> batch, CancellationToken ct)
    {
        if (_options.Elastic.IsTechLogActive)
        {
            var separation = !string.IsNullOrWhiteSpace(_options.TechLog.Separation)
                ? _options.TechLog.Separation
                : _options.Elastic.Separation;

            foreach (var group in batch.GroupBy(d => IndexNamingHelper.BuildIndexName(_options.Elastic.TechLogIndexPrefix, _options.TechLog.IndexId, separation, d.Date)))
            {
                await _elasticPublisher.BulkIndexTechLogAsync(group.Key, group, ct).ConfigureAwait(false);
            }
        }

        if (_options.ClickHouse.IsTechLogActive)
        {
            await _clickHousePublisher.BulkInsertTechLogAsync(batch, ct).ConfigureAwait(false);
        }
    }
}