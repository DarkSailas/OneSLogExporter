using System.Collections.Concurrent;
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
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, FileState> _processedFiles = new(StringComparer.OrdinalIgnoreCase);

    public StateTracker(string stateFilePath)
    {
        _stateFilePath = stateFilePath;
    }

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
    /// Загрузка файла состояния с диска.
    /// </summary>
    public async ValueTask LoadAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                _processedFiles.Clear();
                return;
            }

            var json = await File.ReadAllTextAsync(_stateFilePath, ct).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, FileState>>(json, JsonOptions);
            _processedFiles.Clear();
            if (loaded != null)
            {
                foreach (var (key, value) in loaded)
                {
                    _processedFiles[key] = value;
                }
            }
        }
        catch
        {
            _processedFiles.Clear();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Сохранение текущего состояния на диск.
    /// </summary>
    public async ValueTask SaveAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Снимок словаря гарантирует безопасную сериализацию при параллельной модификации другими воркерами
            var snapshot = new Dictionary<string, FileState>(_processedFiles, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(_stateFilePath, json, ct).ConfigureAwait(false);
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
