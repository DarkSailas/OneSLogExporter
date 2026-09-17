using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Serialization;

namespace OneSLogExporter.Core.Services;

/// <summary>
/// Сервис инкрементальной дозаписи (append) распарсенных логов 1С в локальные JSON-файлы с ротацией по размеру (МБ), числу записей и лимиту файлов.
/// </summary>
public sealed class FileDumper
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly FileDumpSettings _settings;
    private readonly ExporterOptions? _options;
    private readonly ILogger<FileDumper> _logger;
    private readonly SemaphoreSlim _eventLogLock = new(1, 1);
    private readonly SemaphoreSlim _techLogLock = new(1, 1);

    public FileDumper(FileDumpSettings settings, ILogger<FileDumper> logger, ExporterOptions? options = null)
    {
        _settings = settings;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Инкрементальная дозапись пачки записей Журнала Регистрации в локальный файл дампа по заданной маске.
    /// </summary>
    public async ValueTask DumpEventLogsAsync(string prefix, IEnumerable<EventLogDoc> docs, CancellationToken ct = default)
    {
        var isActive = _settings.IsEventLogActive 
            || (_options != null && (_options.ClickHouse.IsEventLogActive || _options.Elastic.IsEventLogActive));
        if (!isActive) return;

        var docList = docs as IReadOnlyCollection<EventLogDoc> ?? docs.ToList();
        if (docList.Count == 0) return;

        await _eventLogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var targetPath = !string.IsNullOrWhiteSpace(_settings.EventLogDirectoryPath)
                ? _settings.EventLogDirectoryPath
                : (!string.IsNullOrWhiteSpace(_settings.DirectoryPath)
                    ? Path.Combine(_settings.DirectoryPath, "eventlog")
                    : "C:/1C_Export/json_dump/eventlog");
            var targetDir = ResolveTargetDirectory(targetPath);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var maxBytes = GetEffectiveMaxFileBytes();
            var maxRecords = GetEffectiveMaxFileRecords();

            var pattern = string.IsNullOrWhiteSpace(_settings.EventLogFileNamePattern) ? "data_evlog_{N}.json" : _settings.EventLogFileNamePattern;
            var (stream, writer, filePath) = OpenDumpWriter(targetDir, pattern, prefix);
            var currentFileRecords = 0;
            var currentFileBytes = stream.Length;
            var totalWritten = 0;

            try
            {
                foreach (var doc in docList)
                {
                    if (doc == null || string.IsNullOrWhiteSpace(doc.Event))
                        continue;

                    string json;
                    try
                    {
                        json = JsonSerializer.Serialize(doc, LogJsonContext.Compact.EventLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ошибка при сериализации записи ЖР [{DocId}]. Пропускаем некорректную запись.", doc.Id);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(json) || json.Length < 10 || json == "{}")
                        continue;

                    // Если файл заполнен по лимиту размера или записей -> ротируем в следующий слот
                    if ((maxBytes > 0 && currentFileBytes >= maxBytes) ||
                        (maxRecords > 0 && currentFileRecords >= maxRecords))
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        await writer.DisposeAsync().ConfigureAwait(false);
                        await stream.DisposeAsync().ConfigureAwait(false);

                        _logger.LogInformation("Файл дампа ЖР достиг лимита ({Bytes:N0} байт / {Rec} записей): {Path}. Ротация в следующий слот.",
                            currentFileBytes, currentFileRecords, filePath);

                        (stream, writer, filePath) = OpenDumpWriter(targetDir, pattern, prefix, forceNewSlot: true);
                        currentFileRecords = 0;
                        currentFileBytes = stream.Length;
                    }

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    currentFileRecords++;
                    currentFileBytes += Encoding.UTF8.GetByteCount(json) + 2;
                    totalWritten++;
                }

                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("Записано {Count} новых валидных записей ЖР в локальные файлы дампа (текущий слот: {FilePath})", totalWritten, filePath);

            CleanupOldDumps(targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при инкрементальной дозаписи дампа Журнала Регистрации");
            throw;
        }
        finally
        {
            _eventLogLock.Release();
        }
    }

    /// <summary>
    /// Инкрементальная выгрузка пачки записей Технологического Журнала в локальный файл дампа по заданной маске.
    /// </summary>
    public async ValueTask DumpTechLogsAsync(string prefix, IEnumerable<TechLogDoc> docs, CancellationToken ct = default)
    {
        var isActive = _settings.IsTechLogActive 
            || (_options != null && (_options.ClickHouse.IsTechLogActive || _options.Elastic.IsTechLogActive));
        if (!isActive) return;

        var docList = docs as IReadOnlyCollection<TechLogDoc> ?? docs.ToList();
        if (docList.Count == 0) return;

        await _techLogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var targetPath = !string.IsNullOrWhiteSpace(_settings.TechLogDirectoryPath)
                ? _settings.TechLogDirectoryPath
                : (!string.IsNullOrWhiteSpace(_settings.DirectoryPath)
                    ? Path.Combine(_settings.DirectoryPath, "techlog")
                    : "C:/1C_Export/json_dump/techlog");
            var targetDir = ResolveTargetDirectory(targetPath);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var maxBytes = GetEffectiveMaxFileBytes();
            var maxRecords = GetEffectiveMaxFileRecords();

            var pattern = string.IsNullOrWhiteSpace(_settings.TechLogFileNamePattern) ? "data_tglog_{N}.json" : _settings.TechLogFileNamePattern;
            var (stream, writer, filePath) = OpenDumpWriter(targetDir, pattern, prefix);
            var currentFileRecords = 0;
            var currentFileBytes = stream.Length;
            var totalWritten = 0;

            try
            {
                foreach (var doc in docList)
                {
                    if (doc == null || string.IsNullOrWhiteSpace(doc.Event))
                        continue;

                    string json;
                    try
                    {
                        json = JsonSerializer.Serialize(doc, LogJsonContext.Compact.TechLogDoc);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Превышение размера полей записи ТЖ [{DocId}] (сообщение: {Msg}). Применяется резервное усечение объекта.", doc.Id, ex.Message);
                        try
                        {
                            var fallbackDoc = SanitizeDocFallback(doc);
                            json = JsonSerializer.Serialize(fallbackDoc, CompactJsonOptions);
                        }
                        catch (Exception fallbackEx)
                        {
                            _logger.LogWarning(fallbackEx, "Критическая ошибка сериализации ТЖ [{DocId}]. Запись пропущена.", doc.Id);
                            continue;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(json) || json.Length < 10 || json == "{}")
                        continue;

                    // Если файл заполнен по лимиту размера или записей -> ротируем в следующий слот
                    if ((maxBytes > 0 && currentFileBytes >= maxBytes) ||
                        (maxRecords > 0 && currentFileRecords >= maxRecords))
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        await writer.DisposeAsync().ConfigureAwait(false);
                        await stream.DisposeAsync().ConfigureAwait(false);

                        _logger.LogInformation("Файл дампа ТЖ достиг лимита ({Bytes:N0} байт / {Rec} записей): {Path}. Ротация в следующий слот.",
                            currentFileBytes, currentFileRecords, filePath);

                        (stream, writer, filePath) = OpenDumpWriter(targetDir, pattern, prefix, forceNewSlot: true);
                        currentFileRecords = 0;
                        currentFileBytes = stream.Length;
                    }

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    currentFileRecords++;
                    currentFileBytes += Encoding.UTF8.GetByteCount(json) + 2;
                    totalWritten++;
                }

                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("Записано {Count} новых валидных записей ТЖ в локальные файлы дампа (текущий слот: {FilePath})", totalWritten, filePath);

            CleanupOldDumps(targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при инкрементальной дозаписи дампа Технологического Журнала");
            throw;
        }
        finally
        {
            _techLogLock.Release();
        }
    }

    private long GetEffectiveMaxFileBytes()
    {
        if (_settings.MaxFileSizeMb > 0)
            return (long)_settings.MaxFileSizeMb * 1024 * 1024;

        if (_settings.MaxTotalSizeMb > 0)
        {
            var limit = _settings.RetainedFileCountLimit > 0 ? _settings.RetainedFileCountLimit : 30;
            return Math.Max(5L * 1024 * 1024, (long)_settings.MaxTotalSizeMb * 1024 * 1024 / limit);
        }

        return 16L * 1024 * 1024; // 16 МБ безопасный размер файла по умолчанию
    }

    private int GetEffectiveMaxFileRecords()
    {
        return _settings.MaxFileRecordCount > 0 ? _settings.MaxFileRecordCount : 0;
    }

    /// <summary>
    /// Определение абсолютного пути каталога относительно корня исполнения приложения.
    /// </summary>
    private static string ResolveTargetDirectory(string dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
            dirPath = "parsed_logs";

        return Path.IsPathRooted(dirPath)
            ? dirPath
            : Path.Combine(AppContext.BaseDirectory, dirPath);
    }

    /// <summary>
    /// Определение целевого файла дампа: циклический выбор слота 1..N при наличии маски {N}, либо инкрементальная ротация.
    /// </summary>
    private (string FilePath, FileMode Mode) GetTargetFile(string targetDir, string pattern, string prefix, bool forceNewSlot = false)
    {
        var now = DateTime.Now;
        var basePattern = pattern
            .Replace("{PREFIX}", prefix, StringComparison.OrdinalIgnoreCase)
            .Replace("{DATE}", now.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{TIME}", now.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{TIMESTAMP}", now.ToString("yyyyMMdd_HHmmss"), StringComparison.OrdinalIgnoreCase);

        if (basePattern.Contains("{N}", StringComparison.OrdinalIgnoreCase))
        {
            var limit = _settings.RetainedFileCountLimit > 0 ? _settings.RetainedFileCountLimit : 30;

            // 1. Поиск первого свободно отсутствующего слота N от 1 до limit
            for (var i = 1; i <= limit; i++)
            {
                var fileName = basePattern.Replace("{N}", i.ToString(), StringComparison.OrdinalIgnoreCase);
                var filePath = Path.Combine(targetDir, fileName);

                if (!File.Exists(filePath))
                    return (filePath, FileMode.Create);
            }

            // 2. Если все слоты 1..limit уже созданы — выбираем самый старый по времени слот для перезаписи по кольцу
            FileInfo? oldestFile = null;
            var oldestTime = DateTime.MaxValue;

            for (var i = 1; i <= limit; i++)
            {
                var fileName = basePattern.Replace("{N}", i.ToString(), StringComparison.OrdinalIgnoreCase);
                var filePath = Path.Combine(targetDir, fileName);
                if (File.Exists(filePath))
                {
                    var info = new FileInfo(filePath);
                    if (info.LastWriteTimeUtc < oldestTime)
                    {
                        oldestTime = info.LastWriteTimeUtc;
                        oldestFile = info;
                    }
                }
            }

            if (oldestFile != null)
            {
                // Освобождаем имя файла через rename-before-delete, чтобы Filebeat и ОС гарантированно получили новый File ID
                SafePrepareFileForRecreation(oldestFile.FullName);
                return (oldestFile.FullName, FileMode.Create);
            }

            var fallbackPath = Path.Combine(targetDir, basePattern.Replace("{N}", "1", StringComparison.OrdinalIgnoreCase));
            SafePrepareFileForRecreation(fallbackPath);
            return (fallbackPath, FileMode.Create);
        }
        else
        {
            var ext = Path.GetExtension(basePattern);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(basePattern);

            var primaryPath = Path.Combine(targetDir, basePattern);
            if (!File.Exists(primaryPath))
                return (primaryPath, FileMode.Create);

            var primaryInfo = new FileInfo(primaryPath);
            if (!forceNewSlot && !ShouldRollFile(primaryInfo))
                return (primaryPath, FileMode.Append);

            for (var i = 1; i <= 99999; i++)
            {
                var indexedFileName = $"{nameWithoutExt}_{i}{ext}";
                var indexedPath = Path.Combine(targetDir, indexedFileName);

                if (!File.Exists(indexedPath))
                    return (indexedPath, FileMode.Create);

                var indexedInfo = new FileInfo(indexedPath);
                if (!forceNewSlot && !ShouldRollFile(indexedInfo))
                    return (indexedPath, FileMode.Append);
            }

            return (primaryPath, FileMode.Append);
        }
    }

    /// <summary>
    /// Безопасное освобождение имени файла перед пересозданием слота дампа (NTFS safe):
    /// переименовывает старый файл во временный, чтобы освободить имя для мгновенного создания нового файла
    /// с новым File ID (inode), предотвращая рассинхронизацию смещений в Filebeat и исключая ошибку DeletePending.
    /// </summary>
    private void SafePrepareFileForRecreation(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Move(filePath, tempPath);
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Если дескриптор еще удерживается внешним процессом, файл удалится при закрытии дескриптора или в CleanupOldDumps
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Предупреждение при ротации файла дампа {Path}: {Msg}", filePath, ex.Message);
        }
    }

    /// <summary>
    /// Безопасное открытие файлового потока дампа с поддержкой повторных попыток при конкурентном чтении.
    /// </summary>
    private (FileStream Stream, StreamWriter Writer, string FilePath) OpenDumpWriter(
        string targetDir,
        string pattern,
        string prefix,
        bool forceNewSlot = false)
    {
        var limit = _settings.RetainedFileCountLimit > 0 ? _settings.RetainedFileCountLimit : 30;
        var maxAttempts = pattern.Contains("{N}", StringComparison.OrdinalIgnoreCase) ? Math.Min(limit, 5) : 3;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var isForced = forceNewSlot || attempt > 0;
            var (filePath, mode) = GetTargetFile(targetDir, pattern, prefix, isForced);

            try
            {
                var stream = new FileStream(filePath, mode, FileAccess.Write, FileShare.ReadWrite, 65536, useAsync: true);
                if (mode == FileMode.Append)
                {
                    stream.Seek(0, SeekOrigin.End);
                }
                var writer = new StreamWriter(stream, Utf8WithoutBom);
                return (stream, writer, filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Файл дампа {Path} временно занят другим процессом ({Error}). Попытка {Attempt}/{MaxAttempts}...",
                    filePath, ex.Message, attempt + 1, maxAttempts);
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        // Резервный аварийный слот с временной меткой для гарантированного сохранения логов без потерь
        var fallbackFileName = $"dump_emergency_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json";
        var fallbackPath = Path.Combine(targetDir, fallbackFileName);
        var fallbackStream = new FileStream(fallbackPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 65536, useAsync: true);
        var fallbackWriter = new StreamWriter(fallbackStream, Utf8WithoutBom);
        return (fallbackStream, fallbackWriter, fallbackPath);
    }


    /// <summary>
    /// Проверка необходимости ротации файла дампа по размеру (МБ) и/или числу записей (строк).
    /// </summary>
    private bool ShouldRollFile(FileInfo info)
    {
        if (!info.Exists)
            return false;

        var maxBytes = GetEffectiveMaxFileBytes();
        var maxRecords = GetEffectiveMaxFileRecords();
        var strategy = _settings.RollStrategy ?? "SizeOrRecordCount";

        var isSizeExceeded = false;
        if (maxBytes > 0 &&
            (strategy.Equals("Size", StringComparison.OrdinalIgnoreCase) ||
             strategy.Equals("SizeOrRecordCount", StringComparison.OrdinalIgnoreCase)))
        {
            isSizeExceeded = info.Length >= maxBytes;
        }

        var isRecordCountExceeded = false;
        if (maxRecords > 0 &&
            (strategy.Equals("RecordCount", StringComparison.OrdinalIgnoreCase) ||
             strategy.Equals("SizeOrRecordCount", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var currentLineCount = File.ReadLines(info.FullName).Count();
                if (currentLineCount >= maxRecords)
                {
                    isRecordCountExceeded = true;
                }
            }
            catch
            {
                // Безопасное проглатывание исключений при доступе к файлу
            }
        }

        if (strategy.Equals("Size", StringComparison.OrdinalIgnoreCase))
            return isSizeExceeded;

        if (strategy.Equals("RecordCount", StringComparison.OrdinalIgnoreCase))
            return isRecordCountExceeded;

        return isSizeExceeded || isRecordCountExceeded;
    }

    /// <summary>
    /// Автоматическое удаление старых файлов дампов при превышении лимита RetainedFileCountLimit и/или MaxTotalSizeMb (в сумме).
    /// </summary>
    private void CleanupOldDumps(string targetDir)
    {
        try
        {
            if (!Directory.Exists(targetDir)) return;

            var files = new DirectoryInfo(targetDir)
                .GetFiles("*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count == 0) return;

            var countLimit = _settings.RetainedFileCountLimit > 0 ? _settings.RetainedFileCountLimit : 30;
            var maxTotalBytes = _settings.MaxTotalSizeMb > 0 ? (long)_settings.MaxTotalSizeMb * 1024 * 1024 : 0;

            // 1. Контроль количества файлов (по умолчанию не более 30 штук в каталоге)
            while (files.Count > countLimit)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                TryDeleteFile(oldest);
            }

            // 2. Контроль суммарного объема файлов в каталоге (по умолчанию максимум 500 МБ в сумме)
            if (maxTotalBytes > 0)
            {
                var currentTotalBytes = files.Sum(f => f.Length);
                while (currentTotalBytes > maxTotalBytes && files.Count > 1)
                {
                    var oldest = files[0];
                    files.RemoveAt(0);
                    TryDeleteFile(oldest);
                }
            }

            // 3. Очистка временных файлов ротации (*.tmp) старше 2 минут
            var tempFiles = new DirectoryInfo(targetDir).GetFiles("*.tmp", SearchOption.TopDirectoryOnly);
            foreach (var tf in tempFiles)
            {
                if (DateTime.UtcNow - tf.LastWriteTimeUtc > TimeSpan.FromMinutes(2))
                {
                    try { tf.Delete(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ошибка при выполнении ротации файлов дампов в {TargetDir}", targetDir);
        }
    }

    private void TryDeleteFile(FileInfo file)
    {
        try
        {
            if (file.IsReadOnly)
            {
                file.IsReadOnly = false;
            }

            file.Delete();
            _logger.LogInformation("Ротация дампов: удален файл дампа {FileName} (соблюдение лимитов количества/размера)", file.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Файл дампа {FileName} временно не может быть удален (заблокирован или занят).", file.Name);
        }
    }

    /// <summary>
    /// Резервное принудительное усечение текстовых полей документа ТЖ до 4 КБ при возникновении переполнения буфера сериализатора.
    /// </summary>
    private static TechLogDoc SanitizeDocFallback(TechLogDoc doc)
    {
        const int cap = 4096;
        var cleanProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in doc.Properties)
        {
            cleanProps[k] = Parsers.TechLogParser.SanitizeText(v, cap);
        }

        return doc with
        {
            Context = Parsers.TechLogParser.SanitizeMultilineText(doc.Context, cap),
            Sql = Parsers.TechLogParser.SanitizeMultilineText(doc.Sql, cap),
            Locks = Parsers.TechLogParser.SanitizeMultilineText(doc.Locks, cap),
            Descr = Parsers.TechLogParser.SanitizeMultilineText(doc.Descr, cap),
            WaitConnections = Parsers.TechLogParser.SanitizeText(doc.WaitConnections, cap),
            Properties = cleanProps
        };
    }
}
