using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace OneSLogExporter.Core.State;

/// <summary>
/// Безопасный менеджер сохранения состояния отсканированных файлов логов 1С с поддержкой трекинга смещения байт (LastPosition).
/// Предотвращает дублирование отправки записей при регулярной работе таймера службы.
/// </summary>
public sealed class StateTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly string _stateFilePath;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, FileState> _processedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public StateTracker(string stateFilePath, ILogger<StateTracker>? logger = null)
    {
        _stateFilePath = stateFilePath;
        _logger = logger;
    }

    /// <summary>
    /// Полный путь к файлу состояния (рядом с ним хранятся файлы фильтра дублей).
    /// </summary>
    public string StateFilePath => _stateFilePath;

    /// <summary>
    /// Состояние отдельного обрабатываемого файла лога с байтовым смещением.
    /// </summary>
    public sealed record FileState
    {
        public required long LastPosition { get; init; }
        public required long LastKnownSize { get; init; }
        public required DateTime LastProcessedUtc { get; init; }
        public DateTime? LastWriteTimeUtc { get; init; }
        public DateTime? CreationTimeUtc { get; init; }
    }

    /// <summary>
    /// Загрузка файла состояния с диска. Выполняется один раз: экземпляр общий для всех воркеров,
    /// и повторная загрузка затёрла бы позиции, которые другой воркер уже обновил в памяти.
    /// Повреждённый файл сохраняется рядом с суффиксом .corrupt, а не молча теряется.
    /// </summary>
    public async ValueTask LoadAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
                return;

            if (File.Exists(_stateFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_stateFilePath, ct).ConfigureAwait(false);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, FileState>>(json, JsonOptions);
                    if (loaded != null)
                    {
                        foreach (var (key, value) in loaded)
                        {
                            _processedFiles[key] = value;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    var backupPath = $"{_stateFilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                    File.Copy(_stateFilePath, backupPath, overwrite: true);
                    _logger?.LogError(ex, "Файл состояния {Path} повреждён, копия сохранена в {Backup}. Чтение начнётся без сохранённых позиций.", _stateFilePath, backupPath);
                }
            }

            _loaded = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Атомарное сохранение текущего состояния на диск (временный файл + замена),
    /// чтобы сбой во время записи не оставил обрезанный JSON.
    /// </summary>
    public async ValueTask SaveAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Снимок словаря гарантирует безопасную сериализацию при параллельной модификации другими воркерами
            var snapshot = new Dictionary<string, FileState>(_processedFiles, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);

            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tempPath = _stateFilePath + ".tmp";
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await fs.WriteAsync(Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
                // Данные на диске до переименования: иначе при сбое питания можно получить пустой файл состояния.
                fs.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _stateFilePath, overwrite: true);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Получение последнего обработанного байтового смещения в файле.
    /// </summary>
    public long GetLastPosition(string filePath)
    {
        var key = Path.GetFullPath(filePath);
        if (_processedFiles.TryGetValue(key, out var state))
        {
            return state.LastPosition;
        }
        return 0;
    }

    /// <summary>
    /// Получение полного состояния обработки для указанного файла.
    /// </summary>
    public FileState? GetFileState(string filePath)
    {
        var key = Path.GetFullPath(filePath);
        return _processedFiles.TryGetValue(key, out var state) ? state : null;
    }

    /// <summary>
    /// Проверка, сохранено ли уже состояние обработки для данного файла.
    /// </summary>
    public bool HasTrackedState(string filePath)
    {
        var key = Path.GetFullPath(filePath);
        return _processedFiles.ContainsKey(key);
    }

    /// <summary>
    /// Проверка, вырос ли файл лога с момента последнего итерационного срабатывания таймера.
    /// </summary>
    public bool HasFileGrown(string filePath, long currentSize)
    {
        var key = Path.GetFullPath(filePath);
        if (!_processedFiles.TryGetValue(key, out var state))
        {
            return currentSize > 0;
        }

        // Для SQLite-баз (.lgd) LastPosition хранит rowID, а не байты. Проверяем изменение физического размера файла:
        if (filePath.EndsWith(".lgd", StringComparison.OrdinalIgnoreCase))
        {
            return currentSize != state.LastKnownSize;
        }

        // Если файл уменьшился в размере (например, был перезаписан/ротирован 1С), перечитываем заново
        if (currentSize < state.LastKnownSize || currentSize < state.LastPosition)
        {
            return true;
        }

        return currentSize > state.LastPosition;
    }

    /// <summary>
    /// Отметка байтового смещения файла как успешно обработанного.
    /// </summary>
    public void MarkFilePosition(string filePath, long lastPosition, long currentSize, DateTime? lastWriteTimeUtc = null, DateTime? creationTimeUtc = null)
    {
        var key = Path.GetFullPath(filePath);
        _processedFiles[key] = new FileState
        {
            LastPosition = lastPosition,
            LastKnownSize = currentSize,
            LastProcessedUtc = DateTime.UtcNow,
            LastWriteTimeUtc = lastWriteTimeUtc,
            CreationTimeUtc = creationTimeUtc
        };
    }

    /// <summary>
    /// Проверка полного завершения обработки файла (обратная совместимость).
    /// </summary>
    public bool IsFileProcessed(string filePath, long currentSize)
    {
        var key = Path.GetFullPath(filePath);
        if (_processedFiles.TryGetValue(key, out var state))
        {
            return state.LastKnownSize == currentSize && state.LastPosition >= currentSize;
        }
        return false;
    }

    /// <summary>
    /// Отметка файла полностью обработанным (обратная совместимость).
    /// </summary>
    public void MarkFileProcessed(string filePath, long currentSize)
    {
        MarkFilePosition(filePath, currentSize, currentSize);
    }
}
