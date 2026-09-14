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
    private readonly ILogger<FileDumper> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileDumper(FileDumpSettings settings, ILogger<FileDumper> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Инкрементальная дозапись пачки записей Журнала Регистрации в локальный файл дампа по заданной маске.
    /// </summary>
    public async ValueTask DumpEventLogsAsync(string prefix, IEnumerable<EventLogDoc> docs, CancellationToken ct = default)
    {
        if (!_settings.IsEventLogActive) return;

        var docList = docs as IReadOnlyCollection<EventLogDoc> ?? docs.ToList();
        if (docList.Count == 0) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
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

            var pattern = string.IsNullOrWhiteSpace(_settings.EventLogFileNamePattern) ? "data_evlog_{N}.json" : _settings.EventLogFileNamePattern;
            var (filePath, mode) = GetTargetFile(targetDir, pattern, prefix);

            var writtenCount = 0;
            await using (var stream = new FileStream(filePath, mode, FileAccess.Write, FileShare.ReadWrite, 65536, useAsync: true))
            await using (var writer = new StreamWriter(stream, Utf8WithoutBom))
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

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    writtenCount++;
                }

                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Записано {Count} новых валидных записей ЖР в локальный файл дампа: {FilePath}", writtenCount, filePath);

            CleanupOldDumps(targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при инкрементальной дозаписи дампа Журнала Регистрации");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Инкрементальная выгрузка пачки записей Технологического Журнала в локальный файл дампа по заданной маске.
    /// </summary>
    public async ValueTask DumpTechLogsAsync(string prefix, IEnumerable<TechLogDoc> docs, CancellationToken ct = default)
    {
        if (!_settings.IsTechLogActive) return;

        var docList = docs as IReadOnlyCollection<TechLogDoc> ?? docs.ToList();
        if (docList.Count == 0) return;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
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

            var pattern = string.IsNullOrWhiteSpace(_settings.TechLogFileNamePattern) ? "data_tglog_{N}.json" : _settings.TechLogFileNamePattern;
            var (filePath, mode) = GetTargetFile(targetDir, pattern, prefix);

            var writtenCount = 0;
            await using (var stream = new FileStream(filePath, mode, FileAccess.Write, FileShare.ReadWrite, 65536, useAsync: true))
            await using (var writer = new StreamWriter(stream, Utf8WithoutBom))
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

                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                    writtenCount++;
                }

                await writer.FlushAsync().ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Записано {Count} новых валидных записей ТЖ в локальный файл дампа: {FilePath}", writtenCount, filePath);

            CleanupOldDumps(targetDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при инкрементальной дозаписи дампа Технологического Журнала");
        }
        finally
        {
            _writeLock.Release();
        }
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
    private (string FilePath, FileMode Mode) GetTargetFile(string targetDir, string pattern, string prefix)
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
                return (oldestFile.FullName, FileMode.Create);
            }

            return (Path.Combine(targetDir, basePattern.Replace("{N}", "1", StringComparison.OrdinalIgnoreCase)), FileMode.Create);
        }
        else
        {
            var ext = Path.GetExtension(basePattern);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(basePattern);

            var primaryPath = Path.Combine(targetDir, basePattern);
            if (!File.Exists(primaryPath))
                return (primaryPath, FileMode.Create);

            var primaryInfo = new FileInfo(primaryPath);
            if (!ShouldRollFile(primaryInfo))
                return (primaryPath, FileMode.Append);

            for (var i = 1; i <= 99999; i++)
            {
                var indexedFileName = $"{nameWithoutExt}_{i}{ext}";
                var indexedPath = Path.Combine(targetDir, indexedFileName);

                if (!File.Exists(indexedPath))
                    return (indexedPath, FileMode.Create);

                var indexedInfo = new FileInfo(indexedPath);
                if (!ShouldRollFile(indexedInfo))
                    return (indexedPath, FileMode.Append);
            }

            return (primaryPath, FileMode.Append);
        }
    }

    /// <summary>
    /// Проверка необходимости ротации файла дампа по размеру (МБ) и/или числу записей (строк).
    /// </summary>
    private bool ShouldRollFile(FileInfo info)
    {
        if (!info.Exists)
            return false;

        var maxFileSizeMb = _settings.MaxFileSizeMb;
        var strategy = _settings.RollStrategy ?? "SizeOrRecordCount";

        var isSizeExceeded = false;
        if (maxFileSizeMb > 0 &&
            (strategy.Equals("Size", StringComparison.OrdinalIgnoreCase) ||
             strategy.Equals("SizeOrRecordCount", StringComparison.OrdinalIgnoreCase)))
        {
            var maxBytes = (long)maxFileSizeMb * 1024 * 1024;
            isSizeExceeded = info.Length >= maxBytes;
        }

        var isRecordCountExceeded = false;
        if (_settings.MaxFileRecordCount > 0 &&
            (strategy.Equals("RecordCount", StringComparison.OrdinalIgnoreCase) ||
             strategy.Equals("SizeOrRecordCount", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var currentLineCount = File.ReadLines(info.FullName).Count();
                if (currentLineCount >= _settings.MaxFileRecordCount)
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
                    currentTotalBytes -= oldest.Length;
                    TryDeleteFile(oldest);
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
            Context = Parsers.TechLogParser.SanitizeText(doc.Context, cap),
            Sql = Parsers.TechLogParser.SanitizeText(doc.Sql, cap),
            Locks = Parsers.TechLogParser.SanitizeText(doc.Locks, cap),
            Descr = Parsers.TechLogParser.SanitizeText(doc.Descr, cap),
            WaitConnections = Parsers.TechLogParser.SanitizeText(doc.WaitConnections, cap),
            Properties = cleanProps
        };
    }
}
