using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
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
    private readonly ConcurrentDictionary<string, int> _batchFailures = new();

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
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Exists)
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToList();

            if (files.Count == 0) return;

            var batchSize = Math.Max(1000, _options.ClickHouse.BulkBatchSize);

            foreach (var fileInfo in files)
            {
                if (ct.IsCancellationRequested) break;

                var file = fileInfo.FullName;
                var state = _stateTracker.GetFileState(file);
                var lastPos = state?.LastPosition ?? 0;

                // Определение пересоздания / ротации файла дампа (усечение размера или новый CreationTime)
                var isRecreated = fileInfo.Length < lastPos
                    || (state?.CreationTimeUtc != null && Math.Abs((fileInfo.CreationTimeUtc - state.CreationTimeUtc.Value).TotalSeconds) > 1);

                if (isRecreated)
                {
                    _logger.LogInformation("Файл дампа ЖР {FileName} был пересоздан/ротирован (размер {Length} < {LastPos} или новый CreationTime). Сброс позиции на начало файла (0 байт).",
                        fileInfo.Name, fileInfo.Length, lastPos);
                    lastPos = 0;
                }
                else if (fileInfo.Length == lastPos)
                {
                    // Новых данных в файле нет
                    continue;
                }

                var fileName = fileInfo.Name;
                var transportedCount = 0;

                await using var fs = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    65536,
                    useAsync: true);

                if (lastPos > 0 && lastPos < fs.Length)
                {
                    fs.Seek(lastPos, SeekOrigin.Begin);
                }
                else
                {
                    lastPos = 0;
                }

                using var reader = new FastLogLineReader(fs, 65536);

                var batch = new List<EventLogDoc>(batchSize);
                var batchStartOffset = lastPos;
                var isFirstLineAfterSeek = lastPos > 0;
                string? line;

                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    if (ct.IsCancellationRequested) break;

                    var span = line.AsSpan().TrimStart(['\uFEFF', ' ', '\t', '\r', '\n']);
                    if (span.IsEmpty) continue;

                    // Защита от поврежденных строк и рассинхронизации смещения: валидная строка NDJSON всегда начинается с '{'
                    if (span[0] != '{')
                    {
                        if (isFirstLineAfterSeek)
                        {
                            _logger.LogWarning("Файл дампа ЖР {FileName}: смещение {LastPos} указывает не на начало JSON-объекта (первый символ: '{Char}'). Сброс позиции на начало файла (0 байт).",
                                fileName, lastPos, span[0]);
                            fs.Seek(0, SeekOrigin.Begin);
                            lastPos = 0;
                            isFirstLineAfterSeek = false;
                            reader.Reset(0);
                            batch.Clear();
                            continue;
                        }

                        _logger.LogWarning("Пропуск некорректной строки (не начинается с '{{') в файле {FileName} со смещения {Offset}: {Preview}",
                            fileName, reader.CurrentLineStartOffset, span.Length > 50 ? span[..50].ToString() : span.ToString());
                        continue;
                    }
                    isFirstLineAfterSeek = false;

                    EventLogDoc? doc = null;
                    try
                    {
                        doc = JsonSerializer.Deserialize(span, LogJsonContext.Compact.EventLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка десериализации записи ЖР из файла {FileName}. Пропускаем строку.", fileName);
                    }

                    if (doc != null)
                    {
                        if (batch.Count == 0)
                        {
                            batchStartOffset = reader.CurrentLineStartOffset;
                        }
                        batch.Add(doc);
                    }

                    if (batch.Count >= batchSize)
                    {
                        var nextOffset = reader.CurrentLineStartOffset;
                        var count = batch.Count;
                        await TrySendEventLogBatchWithDeadLetterAsync(
                            batch, file, fileName, batchStartOffset, nextOffset, fileInfo.Length, fileInfo.LastWriteTimeUtc, fileInfo.CreationTimeUtc, ct).ConfigureAwait(false);
                        transportedCount += count;
                        batch.Clear();
                    }
                }

                if (batch.Count > 0 && !ct.IsCancellationRequested)
                {
                    var count = batch.Count;
                    await TrySendEventLogBatchWithDeadLetterAsync(
                        batch, file, fileName, batchStartOffset, fs.Length, fileInfo.Length, fileInfo.LastWriteTimeUtc, fileInfo.CreationTimeUtc, ct).ConfigureAwait(false);
                    transportedCount += count;
                    batch.Clear();
                }

                if (!ct.IsCancellationRequested)
                {
                    _stateTracker.MarkFilePosition(file, fs.Length, fileInfo.Length, fileInfo.LastWriteTimeUtc, fileInfo.CreationTimeUtc);
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
                .Select(f => new FileInfo(f))
                .Where(fi => fi.Exists)
                .OrderBy(fi => fi.LastWriteTimeUtc)
                .ToList();

            if (files.Count == 0) return;

            var batchSize = Math.Max(1000, _options.ClickHouse.BulkBatchSize);

            foreach (var fileInfo in files)
            {
                if (ct.IsCancellationRequested) break;

                var file = fileInfo.FullName;
                var state = _stateTracker.GetFileState(file);
                var lastPos = state?.LastPosition ?? 0;

                // Определение пересоздания / ротации файла дампа (усечение размера или новый CreationTime)
                var isRecreated = fileInfo.Length < lastPos
                    || (state?.CreationTimeUtc != null && Math.Abs((fileInfo.CreationTimeUtc - state.CreationTimeUtc.Value).TotalSeconds) > 1);

                if (isRecreated)
                {
                    _logger.LogInformation("Файл дампа ТЖ {FileName} был пересоздан/ротирован (размер {Length} < {LastPos} или новый CreationTime). Сброс позиции на начало файла (0 байт).",
                        fileInfo.Name, fileInfo.Length, lastPos);
                    lastPos = 0;
                }
                else if (fileInfo.Length == lastPos)
                {
                    // Новых данных в файле нет
                    continue;
                }

                var fileName = fileInfo.Name;
                var transportedCount = 0;

                await using var fs = new FileStream(
                    file,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    65536,
                    useAsync: true);

                if (lastPos > 0 && lastPos < fs.Length)
                {
                    fs.Seek(lastPos, SeekOrigin.Begin);
                }
                else
                {
                    lastPos = 0;
                }

                using var reader = new FastLogLineReader(fs, 65536);

                var batch = new List<TechLogDoc>(batchSize);
                var batchStartOffset = lastPos;
                var isFirstLineAfterSeek = lastPos > 0;
                string? line;

                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    if (ct.IsCancellationRequested) break;

                    var span = line.AsSpan().TrimStart(['\uFEFF', ' ', '\t', '\r', '\n']);
                    if (span.IsEmpty) continue;

                    // Защита от поврежденных строк и рассинхронизации смещения: валидная строка NDJSON всегда начинается с '{'
                    if (span[0] != '{')
                    {
                        if (isFirstLineAfterSeek)
                        {
                            _logger.LogWarning("Файл дампа ТЖ {FileName}: смещение {LastPos} указывает не на начало JSON-объекта (первый символ: '{Char}'). Сброс позиции на начало файла (0 байт).",
                                fileName, lastPos, span[0]);
                            fs.Seek(0, SeekOrigin.Begin);
                            lastPos = 0;
                            isFirstLineAfterSeek = false;
                            reader.Reset(0);
                            batch.Clear();
                            continue;
                        }

                        _logger.LogWarning("Пропуск некорректной строки (не начинается с '{{') в файле {FileName} со смещения {Offset}: {Preview}",
                            fileName, reader.CurrentLineStartOffset, span.Length > 50 ? span[..50].ToString() : span.ToString());
                        continue;
                    }
                    isFirstLineAfterSeek = false;

                    TechLogDoc? doc = null;
                    try
                    {
                        doc = JsonSerializer.Deserialize(span, LogJsonContext.Compact.TechLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка десериализации записи ТЖ из файла {FileName}. Пропускаем строку.", fileName);
                    }

                    if (doc != null)
                    {
                        if (batch.Count == 0)
                        {
                            batchStartOffset = reader.CurrentLineStartOffset;
                        }
                        batch.Add(doc);
                    }

                    if (batch.Count >= batchSize)
                    {
                        var nextOffset = reader.CurrentLineStartOffset;
                        var count = batch.Count;
                        await TrySendTechLogBatchWithDeadLetterAsync(
                            batch, file, fileName, batchStartOffset, nextOffset, fileInfo.Length, fileInfo.LastWriteTimeUtc, fileInfo.CreationTimeUtc, ct).ConfigureAwait(false);
                        transportedCount += count;
                        batch.Clear();
                    }
                }

                if (batch.Count > 0 && !ct.IsCancellationRequested)
                {
                    var count = batch.Count;
                    await TrySendTechLogBatchWithDeadLetterAsync(
                        batch, file, fileName, batchStartOffset, fs.Length, fileInfo.Length, fileInfo.LastWriteTimeUtc, fileInfo.CreationTimeUtc, ct).ConfigureAwait(false);
                    transportedCount += count;
                    batch.Clear();
                }

                if (!ct.IsCancellationRequested)
                {
                    _stateTracker.MarkFilePosition(file, fs.Length, fileInfo.Length, fileInfo.LastWriteTimeUtc, fileInfo.CreationTimeUtc);
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

    private async Task TrySendEventLogBatchWithDeadLetterAsync(
        List<EventLogDoc> batch,
        string file,
        string fileName,
        long batchStartOffset,
        long nextOffset,
        long fileLength,
        DateTime lastWriteTimeUtc,
        DateTime? creationTimeUtc,
        CancellationToken ct)
    {
        var key = $"{file}:{batchStartOffset}";
        try
        {
            await SendEventLogBatchAsync(batch, ct).ConfigureAwait(false);
            _batchFailures.TryRemove(key, out _);
            _stateTracker.MarkFilePosition(file, nextOffset, fileLength, lastWriteTimeUtc, creationTimeUtc);
            await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var fails = _batchFailures.AddOrUpdate(key, 1, (_, c) => c + 1);
            if (fails >= 3)
            {
                _logger.LogError(ex, "Пачка ЖР ({Count} записей) на смещении {Offset} в файле {FileName} завершилась ошибкой {Fails} раз подряд. Сброс в poison_batches во избежание блокировки конвейера.",
                    batch.Count, batchStartOffset, fileName, fails);
                await SavePoisonBatchAsync("eventlog", fileName, batchStartOffset, batch, ct).ConfigureAwait(false);
                _batchFailures.TryRemove(key, out _);
                _stateTracker.MarkFilePosition(file, nextOffset, fileLength, lastWriteTimeUtc, creationTimeUtc);
                await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Ошибка отправки пачки ЖР на смещении {Offset} в файле {FileName} (попытка {Fails}/3): {Error}",
                    batchStartOffset, fileName, fails, ex.Message);
                throw;
            }
        }
    }

    private async Task TrySendTechLogBatchWithDeadLetterAsync(
        List<TechLogDoc> batch,
        string file,
        string fileName,
        long batchStartOffset,
        long nextOffset,
        long fileLength,
        DateTime lastWriteTimeUtc,
        DateTime? creationTimeUtc,
        CancellationToken ct)
    {
        var key = $"{file}:{batchStartOffset}";
        try
        {
            await SendTechLogBatchAsync(batch, ct).ConfigureAwait(false);
            _batchFailures.TryRemove(key, out _);
            _stateTracker.MarkFilePosition(file, nextOffset, fileLength, lastWriteTimeUtc, creationTimeUtc);
            await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var fails = _batchFailures.AddOrUpdate(key, 1, (_, c) => c + 1);
            if (fails >= 3)
            {
                _logger.LogError(ex, "Пачка ТЖ ({Count} записей) на смещении {Offset} в файле {FileName} завершилась ошибкой {Fails} раз подряд. Сброс в poison_batches во избежание блокировки конвейера.",
                    batch.Count, batchStartOffset, fileName, fails);
                await SavePoisonBatchAsync("techlog", fileName, batchStartOffset, batch, ct).ConfigureAwait(false);
                _batchFailures.TryRemove(key, out _);
                _stateTracker.MarkFilePosition(file, nextOffset, fileLength, lastWriteTimeUtc, creationTimeUtc);
                await _stateTracker.SaveAsync(ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Ошибка отправки пачки ТЖ на смещении {Offset} в файле {FileName} (попытка {Fails}/3): {Error}",
                    batchStartOffset, fileName, fails, ex.Message);
                throw;
            }
        }
    }

    private static async Task SavePoisonBatchAsync<T>(string logType, string fileName, long offset, List<T> items, CancellationToken ct)
    {
        try
        {
            var poisonDir = Path.Combine(AppContext.BaseDirectory, "logs", "poison_batches", logType);
            Directory.CreateDirectory(poisonDir);
            var poisonFile = Path.Combine(poisonDir, $"{Path.GetFileNameWithoutExtension(fileName)}_{offset}.ndjson");
            await using var fs = new FileStream(poisonFile, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await using var writer = new StreamWriter(fs, Encoding.UTF8);
            foreach (var item in items)
            {
                var line = item switch
                {
                    EventLogDoc ev => JsonSerializer.Serialize(ev, LogJsonContext.Compact.EventLogDoc),
                    TechLogDoc tg => JsonSerializer.Serialize(tg, LogJsonContext.Compact.TechLogDoc),
                    _ => JsonSerializer.Serialize(item)
                };
                await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            }
        }
        catch { }
    }
}