using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Serialization;
using OneSLogExporter.Core.Services;

namespace OneSLogExporter.Gui;

/// <summary>
/// Главное окно графического WPF-приложения отладки логов 1С и управления подключениями Elastic/Kibana.
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", ExactSpelling = true, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    /// <summary>
    /// Полный сброс неиспользуемой оперативной памяти и освобождение Working Set процесса в ОС Windows.
    /// </summary>
    public static void DeepGarbageCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        try
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch { }
    }
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<TechLogDoc> _techLogDocs = [];
    private readonly List<EventLogDoc> _eventLogDocs = [];
    private ICollectionView? _tgCollectionView;
    private ICollectionView? _lgCollectionView;
    private CancellationTokenSource? _parsingCts;
    private long _lastProgressUpdateTick;
    private Popup? _lastClosedPopup;
    private long _lastPopupCloseTick;

    private void MenuPopup_Closed(object sender, EventArgs e)
    {
        _lastClosedPopup = sender as Popup;
        _lastPopupCloseTick = Stopwatch.GetTimestamp();
        Mouse.Capture(null);
    }

    private void MenuToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - _lastPopupCloseTick) * 1000.0 / Stopwatch.Frequency;
            // Подавляем повторное открытие ТОЛЬКО если был клик по той же кнопке, чей попап только что закрылся
            if (_lastClosedPopup != null && _lastClosedPopup.PlacementTarget == tb && elapsedMs < 250)
            {
                tb.IsChecked = false;
                e.Handled = true;
                _lastClosedPopup = null;
            }
        }
    }

    private void MenuToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb)
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - _lastPopupCloseTick) * 1000.0 / Stopwatch.Frequency;
            if (_lastClosedPopup != null && _lastClosedPopup.PlacementTarget == tb && elapsedMs < 250)
            {
                tb.IsChecked = false;
                _lastClosedPopup = null;
            }
        }
    }

    private bool _tgHasTimeFrom;
    private bool _tgHasTimeTo;
    private TimeSpan _tgTimeFrom;
    private TimeSpan _tgTimeTo;
    private DateTime _tgExactFrom;
    private DateTime _tgExactTo;

    private bool _lgHasTimeFrom;
    private bool _lgHasTimeTo;
    private TimeSpan _lgTimeFrom;
    private TimeSpan _lgTimeTo;
    private DateTime _lgExactFrom;
    private DateTime _lgExactTo;

    // Интерактивные фильтры календаря по диапазону дат
    private DateTime? _tgFilterDateFrom;
    private DateTime? _tgFilterDateTo;
    private bool _tgHasDateFilter => _tgFilterDateFrom.HasValue || _tgFilterDateTo.HasValue;

    private DateTime? _lgFilterDateFrom;
    private DateTime? _lgFilterDateTo;
    private bool _lgHasDateFilter => _lgFilterDateFrom.HasValue || _lgFilterDateTo.HasValue;

    private bool _isUpdatingDateControls;

    // Снимки активных фильтров для сверхбыстрой сквозной фильтрации на лету
    private LgFilterSnapshot _currentLgSnapshot = new();
    private TgFilterSnapshot _currentTgSnapshot = new();

    // Активные колонки и направления сортировки (Direct In-Memory High-Speed Sorting)
    private readonly List<(string SortMemberPath, ListSortDirection Direction)> _activeTgSorts = [("Date", ListSortDirection.Descending)];
    private readonly List<(string SortMemberPath, ListSortDirection Direction)> _activeLgSorts = [("Date", ListSortDirection.Descending)];

    private readonly ObservableCollection<FilterCheckItem> _tgExcludeEventItems = [];
    private readonly ObservableCollection<FilterCheckItem> _lgExcludeEventItems = [];

    // Коллекции уникальных значений полей ТЖ
    private readonly ObservableCollection<FieldValueItem> _tgUserItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgAppItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgPidItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgSpidItems = [];
    private readonly ObservableCollection<FieldValueItem> _tgThreadItems = [];

    // Коллекции уникальных значений полей ЖР
    private readonly ObservableCollection<FieldValueItem> _lgUserItems = [];
    private readonly ObservableCollection<FieldValueItem> _lgAppItems = [];
    private readonly ObservableCollection<FieldValueItem> _lgMetaItems = [];
    private readonly ObservableCollection<FieldValueItem> _lgEventFieldItems = [];

    // Высокопроизводительные сессионные дисковые кэши SQLite WAL для ТЖ и ЖР
    private SessionCacheService? _tgSessionCache;
    private SessionCacheService? _lgSessionCache;

    public MainWindow()
    {
        InitializeComponent();
        WindowState = WindowState.Maximized;
        TrySetWindowIcon();
        InitAppBuildDate();

        // Очистка старых сессионных кэшей при старте
        try { SessionCacheService.CleanupAllOrphanedTempFiles(); } catch { }

        // Привязка списков отбора по полям
        ListTgUsers.ItemsSource = _tgUserItems;
        ListTgApps.ItemsSource = _tgAppItems;
        ListTgPids.ItemsSource = _tgPidItems;
        ListTgSpids.ItemsSource = _tgSpidItems;
        ListTgThreads.ItemsSource = _tgThreadItems;

        ListLgUsers.ItemsSource = _lgUserItems;
        ListLgApps.ItemsSource = _lgAppItems;
        ListLgMetas.ItemsSource = _lgMetaItems;
        ListLgEvents.ItemsSource = _lgEventFieldItems;

        PopulateTgEventFilterItems();
        PopulateTgExcludeFilterItems();
        PopulateLgEventFilterItems();
        PopulateLgExcludeFilterItems();
        LoadConfigIfPresent();
        LoadGuiState();
        UpdateTgFilterChips();
        UpdateLgFilterChips();

        Closing += (_, _) => SaveAllStateOnClose();
    }

    /// <summary>
    /// Инициализация даты редакции/сборки приложения в правом нижнем углу.
    /// </summary>
    private void InitAppBuildDate()
    {
        try
        {
            var asm = typeof(MainWindow).Assembly;
            var fileLocation = asm.Location;
            DateTime buildDate;
            if (!string.IsNullOrEmpty(fileLocation) && File.Exists(fileLocation))
            {
                buildDate = File.GetLastWriteTime(fileLocation);
            }
            else
            {
                var entryPath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(entryPath) && File.Exists(entryPath))
                {
                    buildDate = File.GetLastWriteTime(entryPath);
                }
                else
                {
                    buildDate = DateTime.Now;
                }
            }
            TxtAppBuildDate.Text = $" • {buildDate:dd.MM.yyyy}";
        }
        catch
        {
            TxtAppBuildDate.Text = $" • {DateTime.Now:dd.MM.yyyy}";
        }
    }

    /// <summary>
    /// Отображение нижней полосы прогресса при выполнении длительных операций парсинга.
    /// </summary>
    private void ShowProgress(string details = "")
    {
        _lastProgressUpdateTick = 0;
        PanelProgress.Visibility = Visibility.Visible;
        BtnCancelGlobal.IsEnabled = true;
        ProgressBarParsing.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtProgressDetails.Text = details;
    }

    /// <summary>
    /// Потокобезопасное плавное обновление процента и деталей прогресс-бара с троттлингом.
    /// </summary>
    private void UpdateProgress(double percent, string details = "")
    {
        var now = Stopwatch.GetTimestamp();
        // Обновляем UI с частотой ~20 fps, чтобы поток отрисовки не зависал
        if (percent < 100.0 && (now - _lastProgressUpdateTick) < (Stopwatch.Frequency / 20))
            return;

        _lastProgressUpdateTick = now;

        Dispatcher.InvokeAsync(() =>
        {
            var clamped = Math.Clamp(percent, 0.0, 100.0);
            ProgressBarParsing.Value = clamped;
            TxtProgressPercent.Text = $"{clamped:F0}%";
            if (!string.IsNullOrEmpty(details))
            {
                TxtProgressDetails.Text = details;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Немедленная отмена текущего парсинга по запросу пользователя.
    /// </summary>
    private void BtnCancelParsing_Click(object sender, RoutedEventArgs e)
    {
        if (_parsingCts != null && !_parsingCts.IsCancellationRequested)
        {
            _parsingCts.Cancel();
            TxtStatus.Text = "⏳ Отправлен сигнал остановки... Завершение текущих потоков чтения.";
            BtnCancelGlobal.IsEnabled = false;
        }
    }

    /// <summary>
    /// Скрытие полосы прогресса по завершении или прерывании операции.
    /// </summary>
    private void HideProgress()
    {
        PanelProgress.Visibility = Visibility.Collapsed;
        ProgressBarParsing.Value = 0;
        TxtProgressPercent.Text = "0%";
        TxtProgressDetails.Text = string.Empty;
    }

    /// <summary>
    /// Безопасная попытка установки иконки окна без выброса исключений при ее отсутствии.
    /// </summary>
    private void TrySetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
            {
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));
            }
        }
        catch
        {
            // Безопасное проглатывание ошибок загрузки визуальной иконки
        }
    }

    /// <summary>
    /// Автоматическая загрузка параметров из файла appsettings.json при наличии.
    /// </summary>
    private void LoadConfigIfPresent()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                OneSLogExporter.Core.Serialization.ConfigSanitizer.SanitizeConfigFile(configPath);
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("Exporter", out var exporter))
                {
                    if (exporter.TryGetProperty("Elastic", out var elastic))
                    {
                        if (elastic.TryGetProperty("ServerUrl", out var url)) TxtElasticUrl.Text = url.GetString() ?? "http://localhost:9200";
                        if (elastic.TryGetProperty("Username", out var user)) TxtElasticUser.Text = user.GetString() ?? string.Empty;
                        if (elastic.TryGetProperty("Password", out var pass)) TxtElasticPass.Password = pass.GetString() ?? string.Empty;
                        if (elastic.TryGetProperty("ApiKey", out var apiKey)) TxtElasticApiKey.Text = apiKey.GetString() ?? string.Empty;
                        var elTg = elastic.TryGetProperty("TechLogEnabled", out var et) && et.GetBoolean();
                        var elEv = elastic.TryGetProperty("EventLogEnabled", out var ee) && ee.GetBoolean();
                        ChkElasticEnabled.IsChecked = elTg || elEv;
                        if (elastic.TryGetProperty("EventLogIndexPrefix", out var evPrefix)) TxtEventLogPrefix.Text = evPrefix.GetString() ?? "events";
                        if (elastic.TryGetProperty("TechLogIndexPrefix", out var tgPrefix)) TxtTechLogPrefix.Text = tgPrefix.GetString() ?? "techlog";
                    }

                    if (exporter.TryGetProperty("Kibana", out var kibana))
                    {
                        if (kibana.TryGetProperty("ServerUrl", out var kUrl)) TxtKibanaUrl.Text = kUrl.GetString() ?? "http://localhost:5601";
                    }

                    if (exporter.TryGetProperty("ClickHouse", out var ch))
                    {
                        if (ch.TryGetProperty("ServerUrl", out var chUrl)) TxtClickHouseUrl.Text = chUrl.GetString() ?? "http://localhost:8123";
                        if (ch.TryGetProperty("Database", out var chDb)) TxtClickHouseDb.Text = chDb.GetString() ?? "default";
                        if (ch.TryGetProperty("User", out var chUser)) TxtClickHouseUser.Text = chUser.GetString() ?? "default";
                        if (ch.TryGetProperty("Password", out var chPass)) TxtClickHousePass.Password = chPass.GetString() ?? string.Empty;
                        if (ch.TryGetProperty("TechLogTable", out var chTg)) TxtClickHouseTechLogTable.Text = chTg.GetString() ?? "techlog";
                        if (ch.TryGetProperty("EventLogTable", out var chEv)) TxtClickHouseEventLogTable.Text = chEv.GetString() ?? "eventlog";
                        if (ch.TryGetProperty("BulkBatchSize", out var chBatch)) TxtClickHouseBatchSize.Text = chBatch.GetInt32().ToString();
                        var chTgEn = ch.TryGetProperty("TechLogEnabled", out var ct) && ct.GetBoolean();
                        var chEvEn = ch.TryGetProperty("EventLogEnabled", out var ce) && ce.GetBoolean();
                        ChkClickHouseEnabled.IsChecked = chTgEn || chEvEn;
                    }

                    if (exporter.TryGetProperty("TechLog", out var tg))
                    {
                        if (tg.TryGetProperty("DirectoryPath", out var tgPath))
                        {
                            var pathStr = tgPath.GetString();
                            if (!string.IsNullOrWhiteSpace(pathStr)) TxtTgPath.Text = pathStr;
                        }
                        if (tg.TryGetProperty("LoadArchive", out var tgArch) && ChkTgLoadArchive != null)
                        {
                            ChkTgLoadArchive.IsChecked = tgArch.GetBoolean();
                        }
                    }

                    if (exporter.TryGetProperty("EventLog", out var evLog))
                    {
                        if (evLog.TryGetProperty("DirectoryPath", out var evPath))
                        {
                            var pathStr = evPath.GetString();
                            if (!string.IsNullOrWhiteSpace(pathStr)) TxtLgPath.Text = pathStr;
                        }
                        if (evLog.TryGetProperty("LoadArchive", out var evArch) && ChkLgLoadArchive != null)
                        {
                            ChkLgLoadArchive.IsChecked = evArch.GetBoolean();
                        }
                    }

                    if (exporter.TryGetProperty("FileDump", out var dump))
                    {
                        if (dump.TryGetProperty("TechLogDirectoryPath", out var tgPath))
                            TxtDumpDirectoryTechLog.Text = tgPath.GetString() ?? "C:/1C_Export/json_dump/techlog";
                        else if (dump.TryGetProperty("DirectoryPath", out var dPath))
                            TxtDumpDirectoryTechLog.Text = (dPath.GetString()?.TrimEnd('/', '\\') ?? "C:/1C_Export/json_dump") + "/techlog";

                        if (dump.TryGetProperty("EventLogDirectoryPath", out var evPath))
                            TxtDumpDirectoryEventLog.Text = evPath.GetString() ?? "C:/1C_Export/json_dump/eventlog";
                        else if (dump.TryGetProperty("DirectoryPath", out var dPath2))
                            TxtDumpDirectoryEventLog.Text = (dPath2.GetString()?.TrimEnd('/', '\\') ?? "C:/1C_Export/json_dump") + "/eventlog";

                        if (dump.TryGetProperty("MaxTotalSizeMb", out var mts)) TxtDumpMaxTotalSizeMb.Text = mts.ToString();
                    }
                }
            }
        }
        catch
        {
            // Ошибки проглатываются для обеспечения устойчивости GUI при старте
        }
    }

    /// <summary>
    /// Формирование объекта настроек ElasticSettings на основе введенных в GUI параметров.
    /// </summary>
    private ElasticSettings BuildElasticSettingsFromUi()
    {
        var isChecked = ChkElasticEnabled.IsChecked ?? false;
        return new ElasticSettings
        {
            TechLogEnabled = isChecked,
            EventLogEnabled = isChecked,
            ServerUrl = string.IsNullOrWhiteSpace(TxtElasticUrl.Text) ? "http://localhost:9200" : TxtElasticUrl.Text.Trim(),
            Username = TxtElasticUser.Text.Trim(),
            Password = TxtElasticPass.Password.Trim(),
            ApiKey = TxtElasticApiKey.Text.Trim(),
            EventLogIndexPrefix = string.IsNullOrWhiteSpace(TxtEventLogPrefix.Text) ? "events" : TxtEventLogPrefix.Text.Trim(),
            TechLogIndexPrefix = string.IsNullOrWhiteSpace(TxtTechLogPrefix.Text) ? "techlog" : TxtTechLogPrefix.Text.Trim(),
            BulkBatchSize = 1000
        };
    }

    /// <summary>
    /// Формирование объекта настроек ClickHouseSettings на основе введенных в GUI параметров.
    /// </summary>
    private ClickHouseSettings BuildClickHouseSettingsFromUi()
    {
        var isChecked = ChkClickHouseEnabled.IsChecked ?? false;
        return new ClickHouseSettings
        {
            TechLogEnabled = isChecked,
            EventLogEnabled = isChecked,
            ServerUrl = ClickHousePublisher.NormalizeServerUrl(TxtClickHouseUrl.Text),
            Database = string.IsNullOrWhiteSpace(TxtClickHouseDb.Text) ? "default" : TxtClickHouseDb.Text.Trim(),
            User = string.IsNullOrWhiteSpace(TxtClickHouseUser.Text) ? "default" : TxtClickHouseUser.Text.Trim(),
            Password = TxtClickHousePass.Password,
            TechLogTable = string.IsNullOrWhiteSpace(TxtClickHouseTechLogTable.Text) ? "techlog" : TxtClickHouseTechLogTable.Text.Trim(),
            EventLogTable = string.IsNullOrWhiteSpace(TxtClickHouseEventLogTable.Text) ? "eventlog" : TxtClickHouseEventLogTable.Text.Trim(),
            BulkBatchSize = int.TryParse(TxtClickHouseBatchSize.Text.Trim(), out var bs) && bs > 0 ? bs : 5000,
            TimeoutSeconds = 60
        };
    }

    /// <summary>
    /// Клик по кнопке "ОБЗОР ▾" Технологического Журнала - открытие выпадающего меню выбора каталога или файла.
    /// </summary>
    private void BtnBrowseTg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void MenuItemBrowseTgFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите каталог Технологического Журнала (TGLogs)"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtTgPath.Text = dialog.FolderName;
        }
    }

    private void MenuItemBrowseTgFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите отдельный файл Технологического Журнала",
            Filter = "Файлы логов и JSON (*.log;*.txt;*.json)|*.log;*.txt;*.json|JSON дампы (*.json)|*.json|Файлы логов (*.log)|*.log|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtTgPath.Text = dialog.FileName;
        }
    }

    /// <summary>
    /// Клик по кнопке "ОБЗОР ▾" Журнала Регистрации - открытие выпадающего меню выбора каталога или файла.
    /// </summary>
    private void BtnBrowseLg_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void MenuItemBrowseLgFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите каталог Журнала Регистрации (LGLogs)"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtLgPath.Text = dialog.FolderName;
        }
    }

    private void MenuItemBrowseLgFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл Журнала Регистрации (.lgp, .lgd, .lgf, .lgx)",
            Filter = "Все форматы ЖР и JSON (*.lgp;1Cv8.lgd;*.json;...)|*.lgp;1Cv8.lgd;*.lgd;*.json;1Cv8.lgf;*.lgx;*.txt|JSON дампы ЖР (*.json)|*.json|Файлы событий (*.lgp)|*.lgp|Базы SQLite (1Cv8.lgd)|1Cv8.lgd;*.lgd|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtLgPath.Text = dialog.FileName;
        }
    }

    private void TxtPath_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void TxtTgPath_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            TxtTgPath.Text = files[0];
            e.Handled = true;
        }
    }

    private void TxtLgPath_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0)
        {
            TxtLgPath.Text = files[0];
            e.Handled = true;
        }
    }

    /// <summary>
    /// Интеллектуальное извлечение даты файла ТЖ из имени файла или времени записи.
    /// </summary>
    private static DateTime? TryExtractTechLogFileDate(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var m = System.Text.RegularExpressions.Regex.Match(name, @"(?<!\d)(\d{2})(\d{2})(\d{2})(\d{2})?(?!\d)");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var yy) &&
            int.TryParse(m.Groups[2].Value, out var mm) && mm is >= 1 and <= 12 &&
            int.TryParse(m.Groups[3].Value, out var dd) && dd is >= 1 and <= 31)
        {
            try { return new DateTime(2000 + yy, mm, dd); } catch { }
        }
        try { return File.GetLastWriteTime(filePath).Date; } catch { return null; }
    }

    /// <summary>
    /// Интеллектуальное извлечение даты файла ЖР из имени файла или времени записи.
    /// </summary>
    private static DateTime? TryExtractEventLogFileDate(string filePath) => LogDiscovery.TryExtractEventLogFileDate(filePath);

    public static List<string> FilterEventLogFilesByDate(List<string> files, DateTime? filterFrom, DateTime? filterTo)
        => LogDiscovery.FilterEventLogFilesByDate(files, filterFrom, filterTo);

    /// <summary>
    /// Высокопроизводительный потоковый парсинг Технологического Журнала с возможностью мгновенной отмены.
    /// </summary>
    private async void BtnParseTg_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtTgPath.Text.Trim();
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            MessageBox.Show($"Путь к ТЖ не найден: {path}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _parsingCts = new CancellationTokenSource();
        var ct = _parsingCts.Token;

        BtnParseTg.IsEnabled = false;
        BtnCancelTg.Visibility = Visibility.Visible;
        TxtStatus.Text = File.Exists(path)
            ? $"Потоковый разбор файла ТЖ: {Path.GetFileName(path)}..."
            : "Поиск и потоковый разбор файлов Технологического Журнала...";
        _techLogDocs.Clear();
        _techLogDocs.TrimExcess();
        _tgSessionCache?.Dispose();
        _tgSessionCache = SessionCacheService.CreateTechLogCache();
        UpdateTempSizeBadge();
        DeepGarbageCollection();
        GridTg.ItemsSource = null;
        ShowProgress("Подготовка и сканирование файлов ТЖ...");

        try
        {
            var maxDocs = GetPreviewLimit(CmbTgLimit);
            var effectiveMax = maxDocs; // Без искусственных ограничений: "Все записи" парсит весь массив полностью
            var tgFilter = BuildTgFilterSnapshot();
            var loadArchiveTg = ChkTgLoadArchive?.IsChecked == true;

            if (maxDocs == int.MaxValue)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtStatus.Text = "ℹ️ Запуск в режиме «Все записи» (без ограничений)...";
                });
            }

            var channel = Channel.CreateBounded<TechLogDoc>(new BoundedChannelOptions(100_000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

            var workerTask = Task.Run(async () =>
            {
                try
                {
                    var logItems = LogDiscovery.FindTechLogFiles(path).ToList();
                    if (logItems.Count == 0 && File.Exists(path))
                    {
                        var (pName, pId) = LogDiscovery.ParseProcessInfo(path);
                        logItems.Add((path, pName, pId, Path.GetFileName(Path.GetDirectoryName(path) ?? "default") ?? "default"));
                    }

                    if (logItems.Count == 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show($"Файлы Технологического Журнала не найдены по пути: {path}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Information);
                            TxtStatus.Text = "Файлы ТЖ не найдены.";
                            HideProgress();
                        });
                        return;
                    }

                    if (logItems.Count > 1)
                    {
                        logItems = logItems.OrderByDescending(x => TryExtractTechLogFileDate(x.FilePath) ?? DateTime.MinValue).ToList();
                        if (_tgFilterDateFrom.HasValue || _tgFilterDateTo.HasValue)
                        {
                            var minDate = _tgFilterDateFrom?.Date ?? DateTime.MinValue;
                            var maxDate = _tgFilterDateTo?.Date ?? DateTime.MaxValue;
                            var filtered = logItems.Where(item =>
                            {
                                var d = TryExtractTechLogFileDate(item.FilePath);
                                return !d.HasValue || (d.Value >= minDate && d.Value <= maxDate);
                            }).ToList();
                            if (filtered.Count > 0) logItems = filtered;
                        }
                        else if (!loadArchiveTg)
                        {
                            // LoadArchive = false (по умолчанию): отбираем только свежие логи за последние сутки
                            var cutoff = DateTime.Today.AddDays(-1);
                            var recent = logItems.Where(item =>
                            {
                                var d = TryExtractTechLogFileDate(item.FilePath);
                                return !d.HasValue || d.Value >= cutoff;
                            }).ToList();
                            if (recent.Count > 0) logItems = recent;
                        }
                    }

                    long totalBytes = 0;
                    foreach (var item in logItems)
                    {
                        try { totalBytes += new FileInfo(item.FilePath).Length; } catch { }
                    }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        TxtStatus.Text = $"Разбор {logItems.Count} файлов ТЖ ({FormatFileSize(totalBytes)})...";
                    });

                    var maxDegree = Math.Clamp(Environment.ProcessorCount, 2, 16);
                    long processedBytes = 0;
                    int completedFiles = 0;

                    await Parallel.ForEachAsync(logItems, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxDegree,
                        CancellationToken = ct
                    }, async (item, token) =>
                    {
                        long fileLength = 0;
                        try { fileLength = new FileInfo(item.FilePath).Length; } catch { }

                        if (item.FilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            await foreach (var doc in TechLogParser.ParseJsonDumpAsync(item.FilePath, ct: token).ConfigureAwait(false))
                            {
                                if (tgFilter.HasAnyCriteria && !tgFilter.Matches(doc))
                                    continue;

                                if (!channel.Writer.TryWrite(doc))
                                {
                                    await channel.Writer.WriteAsync(doc, token).ConfigureAwait(false);
                                }
                            }
                        }
                        else
                        {
                            await foreach (var doc in TechLogParser.ParseFileAsync(item.FilePath, item.ProcessName, item.ProcessId, ct: token).ConfigureAwait(false))
                            {
                                if (tgFilter.HasAnyCriteria && !tgFilter.Matches(doc))
                                    continue;

                                if (!channel.Writer.TryWrite(doc))
                                {
                                    await channel.Writer.WriteAsync(doc, token).ConfigureAwait(false);
                                }
                            }
                        }

                        var currentDone = Interlocked.Increment(ref completedFiles);
                        var currentBytes = Interlocked.Add(ref processedBytes, fileLength);
                        var filePct = logItems.Count > 0 ? (double)currentDone / logItems.Count * 100.0 : 0.0;
                        var bytePct = totalBytes > 0 ? (double)currentBytes / totalBytes * 100.0 : 0.0;
                        var pct = Math.Max(filePct, bytePct);
                        var tgCount = _tgSessionCache?.TotalCount ?? _techLogDocs.Count;
                        var notice = maxDocs == int.MaxValue && tgCount >= 500_000 ? " • ⚠️ Режим «Все записи» (кэш на SSD)" : "";
                        UpdateProgress(pct, $"Файлов {currentDone}/{logItems.Count} ({FormatFileSize(currentBytes)} / {FormatFileSize(totalBytes)}) • Отобрано: {tgCount:N0}{notice}");
                    });
                }
                catch (OperationCanceledException) { }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            var isUnlimitedMode = maxDocs == int.MaxValue;
            const int maxUiPreview = 100_000;
            var targetUiCap = isUnlimitedMode ? maxUiPreview : maxDocs;

            var consumerTask = Task.Run(async () =>
            {
                var batch = isUnlimitedMode ? new List<TechLogDoc>(20_000) : null;
                var uiBatch = new List<TechLogDoc>(5000);
                var lastUiTick = Stopwatch.GetTimestamp();
                int uiAdded = 0;

                try
                {
                    while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        while (channel.Reader.TryRead(out var doc))
                        {
                            if (isUnlimitedMode)
                            {
                                batch!.Add(doc);
                            }

                            if (uiAdded < targetUiCap)
                            {
                                uiBatch.Add(doc);
                                uiAdded++;
                            }

                            if (!isUnlimitedMode && uiAdded >= targetUiCap)
                            {
                                _parsingCts?.Cancel();
                                break;
                            }

                            if (isUnlimitedMode && batch!.Count >= 20_000)
                            {
                                if (_tgSessionCache != null)
                                {
                                    await _tgSessionCache.InsertTechLogsAsync(batch, ct).ConfigureAwait(false);
                                }
                                batch.Clear();

                                if (uiBatch.Count > 0)
                                {
                                    var staging = uiBatch.ToArray();
                                    uiBatch.Clear();
                                    _ = Dispatcher.InvokeAsync(() =>
                                    {
                                        _techLogDocs.AddRange(staging);
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }

                                var now = Stopwatch.GetTimestamp();
                                if ((now - lastUiTick) * 1000.0 / Stopwatch.Frequency >= 120)
                                {
                                    lastUiTick = now;
                                    _ = Dispatcher.InvokeAsync(() =>
                                    {
                                        UpdateTgCountStatus();
                                        UpdateTempSizeBadge();
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }
                            }
                            else if (!isUnlimitedMode && uiBatch.Count >= 5000)
                            {
                                var staging = uiBatch.ToArray();
                                uiBatch.Clear();
                                _ = Dispatcher.InvokeAsync(() =>
                                {
                                    _techLogDocs.AddRange(staging);
                                    UpdateTgCountStatus();
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }

                        if (!isUnlimitedMode && uiAdded >= targetUiCap)
                        {
                            break;
                        }

                        if (isUnlimitedMode && batch != null && batch.Count > 0)
                        {
                            var now = Stopwatch.GetTimestamp();
                            if ((now - lastUiTick) * 1000.0 / Stopwatch.Frequency >= 250)
                            {
                                if (_tgSessionCache != null)
                                {
                                    await _tgSessionCache.InsertTechLogsAsync(batch, ct).ConfigureAwait(false);
                                }
                                batch.Clear();

                                if (uiBatch.Count > 0)
                                {
                                    var staging = uiBatch.ToArray();
                                    uiBatch.Clear();
                                    _ = Dispatcher.InvokeAsync(() =>
                                    {
                                        _techLogDocs.AddRange(staging);
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }

                                lastUiTick = now;
                                _ = Dispatcher.InvokeAsync(() =>
                                {
                                    UpdateTgCountStatus();
                                    UpdateTempSizeBadge();
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }
                    }

                    if (isUnlimitedMode && batch != null && batch.Count > 0)
                    {
                        if (_tgSessionCache != null)
                        {
                            await _tgSessionCache.InsertTechLogsAsync(batch, ct).ConfigureAwait(false);
                        }
                        batch.Clear();
                    }

                    if (uiBatch.Count > 0)
                    {
                        var staging = uiBatch.ToArray();
                        uiBatch.Clear();
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _techLogDocs.AddRange(staging);
                            UpdateTgCountStatus();
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);

            try
            {
                await Task.WhenAll(workerTask, consumerTask);
            }
            catch (OperationCanceledException) { }

            if (_tgSessionCache != null && _tgSessionCache.TotalCount > 0)
            {
                try
                {
                    await _tgSessionCache.EnsureIndexesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }

            UpdateTempSizeBadge();
            var totalTgCount = (_tgSessionCache != null && _tgSessionCache.TotalCount > 0) ? _tgSessionCache.TotalCount : _techLogDocs.Count;
            if (totalTgCount > _techLogDocs.Count)
            {
                TxtStatus.Text = $"Разбор ТЖ завершен. Всего записей: {totalTgCount:N0} (кэш на SSD). В таблице отображаются первые {_techLogDocs.Count:N0} записей.";
            }
            else
            {
                TxtStatus.Text = $"Разбор ТЖ завершен. Загружено {totalTgCount:N0} записей.";
            }

            if (_techLogDocs.Count > 100_000)
            {
                TxtStatus.Text = $"Подготовка таблицы и фасетных фильтров для {_techLogDocs.Count:N0} записей...";
                await Task.Yield();
            }

            // Сортировка по активным колонкам (Direct In-Memory Sort)
            if (_activeTgSorts.Count > 0)
            {
                if (_techLogDocs.Count > 100_000)
                    await Task.Run(() => SortTechLogDocs(_techLogDocs, _activeTgSorts));
                else
                    SortTechLogDocs(_techLogDocs, _activeTgSorts);
            }
            else
            {
                ApplyTgSortPreset();
            }

            PopulateTgEventFilterItems();
            PopulateTgExcludeFilterItems();
            PopulateTgFieldValues();
            _currentTgSnapshot = BuildTgFilterSnapshot();
            _tgCollectionView = CollectionViewSource.GetDefaultView(_techLogDocs);
            _tgCollectionView.Filter = FilterTgDoc;

            foreach (var col in GridTg.Columns)
            {
                var match = _activeTgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }

            GridTg.ItemsSource = _tgCollectionView;
            UpdateTgFilterChips();
            UpdateTgCountStatus();
            if (_techLogDocs.Count > 0)
            {
                GridTg.SelectedIndex = 0;
            }

            if (ct.IsCancellationRequested)
            {
                TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем! Загружено {_techLogDocs.Count} записей.";
            }
            else
            {
                TxtStatus.Text = $"Парсинг ТЖ завершен! Загружено {_techLogDocs.Count} записей (отсортировано: сначала новые).";
            }
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем. Загружено {_techLogDocs.Count} записей.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при парсинге ТЖ: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка парсинга ТЖ.";
        }
        finally
        {
            HideProgress();
            BtnParseTg.IsEnabled = true;
            BtnCancelTg.Visibility = Visibility.Collapsed;
            DeepGarbageCollection();
            _parsingCts?.Dispose();
            _parsingCts = null;
        }
    }

    /// <summary>
    /// Извлечение численного лимита количества записей из ComboBox.
    /// </summary>
    private static int GetPreviewLimit(ComboBox cmb)
    {
        if (cmb.SelectedItem is ComboBoxItem item && item.Content != null)
        {
            var text = item.Content.ToString() ?? string.Empty;
            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var val) && val > 0)
                return val;
        }
        return int.MaxValue; // Без ограничений ("Все записи")
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

    /// <summary>
    /// Обновление бейджа размера сессионного кэша на накопителе (BadgeTempSize в статус-баре).
    /// </summary>
    private void UpdateTempSizeBadge()
    {
        long totalBytes = 0;
        if (_tgSessionCache != null)
            totalBytes += _tgSessionCache.GetCacheFileSizeBytes();
        if (_lgSessionCache != null)
            totalBytes += _lgSessionCache.GetCacheFileSizeBytes();

        if (totalBytes > 0)
        {
            TxtTempFileSize.Text = FormatFileSize(totalBytes);
            BadgeTempSize.Visibility = Visibility.Visible;
        }
        else
        {
            BadgeTempSize.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Клик по бейджу TEMP: открытие каталога временных файлов сессии в Проводнике Windows.
    /// </summary>
    private void BadgeTempSize_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var tempDir = SessionCacheService.GetDefaultTempDirectory();
            if (Directory.Exists(tempDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = tempDir,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    /// <summary>
    /// Высокопроизводительный потоковый парсинг Журнала Регистрации с возможностью мгновенной отмены.
    /// </summary>
    private async void BtnParseLg_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtLgPath.Text.Trim();
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            MessageBox.Show($"Путь к ЖР не найден: {path}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _parsingCts = new CancellationTokenSource();
        var ct = _parsingCts.Token;

        BtnParseLg.IsEnabled = false;
        BtnCancelLg.Visibility = Visibility.Visible;
        TxtStatus.Text = File.Exists(path)
            ? $"Потоковый разбор файла ЖР: {Path.GetFileName(path)}..."
            : "Поиск словаря и параллельный парсинг файлов Журнала Регистрации...";
        _eventLogDocs.Clear();
        _eventLogDocs.TrimExcess();
        _lgSessionCache?.Dispose();
        _lgSessionCache = SessionCacheService.CreateEventLogCache();
        UpdateTempSizeBadge();
        DeepGarbageCollection();
        GridLg.ItemsSource = null;
        ShowProgress("Подготовка к сканированию файлов Журнала Регистрации...");

        try
        {
            var maxDocs = GetPreviewLimit(CmbLgLimit);
            var effectiveMax = maxDocs; // Без искусственных ограничений: "Все записи" парсит весь массив полностью
            var lgFilter = BuildLgFilterSnapshot();
            var loadArchiveLg = ChkLgLoadArchive?.IsChecked == true;

            if (maxDocs == int.MaxValue)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtStatus.Text = "ℹ️ Запуск в режиме «Все записи» (без ограничений)...";
                });
            }

            var channel = Channel.CreateBounded<EventLogDoc>(new BoundedChannelOptions(100_000)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });

            var fileBytesRead = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var fileBytesSkipped = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            var workerTask = Task.Run(async () =>
            {
                try
                {
                    // 1. Поиск словаря в фоновом потоке
                    var dictPath = LogDiscovery.FindEventLogDictionary(path);
                    LgfDictionary dict;
                    if (!string.IsNullOrEmpty(dictPath) && File.Exists(dictPath))
                    {
                        var dictProgress = new Progress<(long BytesRead, long TotalBytes)>(p =>
                        {
                            var pct = p.TotalBytes > 0 ? (double)p.BytesRead / p.TotalBytes * 100.0 : 0.0;
                            UpdateProgress(pct, $"Словарь 1Cv8.lgf: {FormatFileSize(p.BytesRead)} / {FormatFileSize(p.TotalBytes)}");
                        });
                        dict = await EventLogParser.ParseDictionaryAsync(dictPath, dictProgress, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        dict = new LgfDictionary();
                        await Dispatcher.InvokeAsync(() => TxtStatus.Text = "ℹ️ Словарь 1Cv8.lgf не найден — автономный разбор по структуре .lgp...");
                    }

                    if (ct.IsCancellationRequested) return;

                    // 2. Поиск файлов в фоновом потоке
                    var lgpFiles = LogDiscovery.FindEventLogFiles(path).ToList();
                    if (lgpFiles.Count == 0 && File.Exists(path))
                    {
                        lgpFiles.Add(path);
                    }

                    if (lgpFiles.Count == 0)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            MessageBox.Show($"Файлы событий Журнала Регистрации не найдены по пути: {path}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Information);
                            TxtStatus.Text = "Файлы ЖР не найдены.";
                            HideProgress();
                        });
                        return;
                    }

                    // Если файлов несколько — фильтруем их по интервалам времени 1С
                    if (lgpFiles.Count > 1)
                    {
                        lgpFiles = FilterEventLogFilesByDate(lgpFiles, _lgFilterDateFrom, _lgFilterDateTo);
                        if (!loadArchiveLg && !_lgFilterDateFrom.HasValue && !_lgFilterDateTo.HasValue && lgpFiles.Count > 1)
                        {
                            var latestDate = lgpFiles.Select(TryExtractEventLogFileDate).Where(d => d.HasValue).Max();
                            if (latestDate.HasValue)
                            {
                                var threshold = latestDate.Value.Date;
                                var recentFiles = lgpFiles.Where(f => (TryExtractEventLogFileDate(f) ?? DateTime.MinValue) >= threshold).ToList();
                                if (recentFiles.Count > 0)
                                    lgpFiles = recentFiles;
                                else
                                    lgpFiles = [lgpFiles[^1]];
                            }
                            else
                            {
                                lgpFiles = [lgpFiles[^1]];
                            }
                        }
                    }

                    long totalBytes = 0;
                    foreach (var f in lgpFiles)
                    {
                        try { totalBytes += new FileInfo(f).Length; } catch { }
                    }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        TxtStatus.Text = $"Разбор {lgpFiles.Count} файлов ЖР ({FormatFileSize(totalBytes)})...";
                    });

                    var maxDegree = Math.Clamp(Environment.ProcessorCount, 2, 12);

                    int completedFiles = 0;
                    long lastUiUpdateTick = 0;

                    if (lgpFiles.Count == 1 &&
                        !lgpFiles[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                        !lgpFiles[0].EndsWith(".lgd", StringComparison.OrdinalIgnoreCase))
                    {
                        var singleFile = lgpFiles[0];

                        // Режим 1: Мгновенная загрузка свежих / live записей (LoadArchive = false и нет явного фильтра по дате С)
                        if (!loadArchiveLg && !_lgFilterDateFrom.HasValue && totalBytes > 20 * 1024 * 1024)
                        {
                            long startOffset = 0;
                            await using (var fs = new FileStream(singleFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                var todayOffset = await EventLogParser.FindFastStartOffsetAsync(fs, DateTime.Today, ct).ConfigureAwait(false);
                                if (todayOffset > 0 && todayOffset < totalBytes - 64 * 1024)
                                {
                                    startOffset = todayOffset;
                                }
                                else
                                {
                                    // Если в файле нет записей за сегодня (архивный снимок или выходные) -> берем хвост файла
                                    long tailBytes = Math.Min(totalBytes, maxDocs != int.MaxValue 
                                        ? Math.Clamp((long)maxDocs * 800, 10 * 1024 * 1024, 100 * 1024 * 1024) 
                                        : 50 * 1024 * 1024);
                                    startOffset = Math.Max(0, totalBytes - tailBytes);
                                }
                            }

                            var fileProgress = new Progress<EventLogParser.LogReadProgress>(p =>
                            {
                                var activeRead = Math.Max(0, p.BytesRead - p.SkippedBytes);
                                var targetTotal = Math.Max(1, totalBytes - p.SkippedBytes);
                                var count = _lgSessionCache?.TotalCount ?? _eventLogDocs.Count;
                                var pct = Math.Clamp((double)activeRead / targetTotal * 100.0, 0.0, 100.0);
                                UpdateProgress(pct, $"⚡ Свежие записи (live): {FormatFileSize(activeRead)} / {FormatFileSize(targetTotal)} ({pct:F1}%) • Отобрано: {count:N0}");
                            });

                            await foreach (var doc in EventLogParser.ParseLogAsync(singleFile, dict, fileProgress, ct, startOffset: startOffset).ConfigureAwait(false))
                            {
                                if (lgFilter.HasAnyCriteria && !lgFilter.Matches(doc))
                                    continue;

                                if (!channel.Writer.TryWrite(doc))
                                {
                                    await channel.Writer.WriteAsync(doc, ct).ConfigureAwait(false);
                                }
                            }

                            UpdateProgress(100.0, $"Готово • Отобрано: {(_lgSessionCache?.TotalCount ?? _eventLogDocs.Count):N0}");
                        }
                        // Режим 2: Высокоскоростной параллельный разбор файла на N независимых чанков (когда включен [x] АРХИВ и нет даты С)
                        else if (loadArchiveLg && !_lgFilterDateFrom.HasValue && totalBytes > 100 * 1024 * 1024)
                        {
                            int numChunks = Math.Clamp(Environment.ProcessorCount, 4, 12);
                            long chunkSize = totalBytes / numChunks;
                            var chunks = Enumerable.Range(0, numChunks).Select(i => (
                                Index: i,
                                Start: i * chunkSize,
                                End: (i == numChunks - 1) ? long.MaxValue : (i + 1) * chunkSize
                            )).ToList();

                            var chunkBytesRead = new long[numChunks];
                            long lastChunkTick = 0;

                            await Parallel.ForEachAsync(chunks, new ParallelOptions
                            {
                                MaxDegreeOfParallelism = numChunks,
                                CancellationToken = ct
                            }, async (chunk, token) =>
                            {
                                var chunkProgress = new Progress<EventLogParser.LogReadProgress>(p =>
                                {
                                    Volatile.Write(ref chunkBytesRead[chunk.Index], Math.Max(0, p.BytesRead - chunk.Start));
                                    var now = Stopwatch.GetTimestamp();
                                    if ((now - Volatile.Read(ref lastChunkTick)) * 1000.0 / Stopwatch.Frequency >= 80)
                                    {
                                        Volatile.Write(ref lastChunkTick, now);
                                        long curTotal = 0;
                                        for (int j = 0; j < numChunks; j++) curTotal += Volatile.Read(ref chunkBytesRead[j]);
                                        var count = _lgSessionCache?.TotalCount ?? _eventLogDocs.Count;
                                        var pct = Math.Clamp((double)curTotal / totalBytes * 100.0, 0.0, 100.0);
                                        var notice = maxDocs == int.MaxValue && count >= 500_000 ? " • ⚠️ Режим «Все записи» (кэш на SSD)" : "";
                                        UpdateProgress(pct, $"⚡ Параллельный разбор ({numChunks} потоков): {FormatFileSize(curTotal)} / {FormatFileSize(totalBytes)} ({pct:F1}%) • Отобрано: {count:N0}{notice}");
                                    }
                                });

                                await foreach (var doc in EventLogParser.ParseLogAsync(singleFile, dict, chunkProgress, token, startOffset: chunk.Start, endOffset: chunk.End).ConfigureAwait(false))
                                {
                                    if (lgFilter.HasAnyCriteria && !lgFilter.Matches(doc))
                                        continue;

                                    if (!channel.Writer.TryWrite(doc))
                                    {
                                        await channel.Writer.WriteAsync(doc, token).ConfigureAwait(false);
                                    }
                                }
                            }).ConfigureAwait(false);

                            long finalTotal = 0;
                            for (int j = 0; j < numChunks; j++) finalTotal += Volatile.Read(ref chunkBytesRead[j]);
                            var finalCount = _lgSessionCache?.TotalCount ?? _eventLogDocs.Count;
                            UpdateProgress(100.0, $"Разбор завершен ({FormatFileSize(totalBytes)}) • Отобрано: {finalCount:N0}");
                        }
                        else
                        {
                            // Обычный разбор одиночного файла (если файл небольшой или задан фильтр по дате)
                            var fileProgress = new Progress<EventLogParser.LogReadProgress>(p =>
                            {
                                fileBytesRead[singleFile] = p.BytesRead;
                                fileBytesSkipped[singleFile] = p.SkippedBytes;
                                var now = Stopwatch.GetTimestamp();
                                if ((now - lastUiUpdateTick) * 1000.0 / Stopwatch.Frequency >= 80)
                                {
                                    lastUiUpdateTick = now;
                                    long curTotal = p.BytesRead;
                                    long skippedTotal = p.SkippedBytes;
                                    long activeRead = Math.Max(0, curTotal - skippedTotal);
                                    long targetTotal = Math.Max(1, totalBytes - skippedTotal);
                                    var count = _lgSessionCache?.TotalCount ?? _eventLogDocs.Count;
                                    var notice = maxDocs == int.MaxValue && count >= 500_000 ? " • ⚠️ Режим «Все записи» (кэш на SSD)" : "";

                                    if (skippedTotal > 0)
                                    {
                                        var pct = Math.Clamp((double)activeRead / targetTotal * 100.0, 0.0, 100.0);
                                        UpdateProgress(pct, $"⚡ Пропущено: {FormatFileSize(skippedTotal)} • Прочитано: {FormatFileSize(activeRead)} / {FormatFileSize(targetTotal)} ({pct:F1}%) • Отобрано: {count:N0}{notice}");
                                    }
                                    else
                                    {
                                        var pct = totalBytes > 0 ? (double)curTotal / totalBytes * 100.0 : 0.0;
                                        UpdateProgress(pct, $"{FormatFileSize(curTotal)} / {FormatFileSize(totalBytes)} ({pct:F1}%) • Отобрано: {count:N0}{notice}");
                                    }
                                }
                            });

                            await foreach (var doc in EventLogParser.ParseLogAsync(singleFile, dict, fileProgress, ct, _lgFilterDateFrom, _lgFilterDateTo).ConfigureAwait(false))
                            {
                                if (lgFilter.HasAnyCriteria && !lgFilter.Matches(doc))
                                    continue;

                                if (!channel.Writer.TryWrite(doc))
                                {
                                    await channel.Writer.WriteAsync(doc, ct).ConfigureAwait(false);
                                }
                            }

                            UpdateProgress(100.0, $"Файлов 1/1 ({FormatFileSize(totalBytes)}) • Отобрано: {(_lgSessionCache?.TotalCount ?? _eventLogDocs.Count):N0}");
                        }
                    }
                    else
                    {
                        // Множественные файлы (или .lgd / .json)
                        await Parallel.ForEachAsync(lgpFiles, new ParallelOptions
                        {
                            MaxDegreeOfParallelism = maxDegree,
                            CancellationToken = ct
                        }, async (lgpFile, token) =>
                        {
                            long fileLength = 0;
                            try { fileLength = new FileInfo(lgpFile).Length; } catch { }

                            if (lgpFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            {
                                await foreach (var doc in EventLogParser.ParseJsonDumpAsync(lgpFile, ct: token).ConfigureAwait(false))
                                {
                                    if (lgFilter.HasAnyCriteria && !lgFilter.Matches(doc))
                                        continue;

                                    if (!channel.Writer.TryWrite(doc))
                                    {
                                        await channel.Writer.WriteAsync(doc, token).ConfigureAwait(false);
                                    }
                                }
                            }
                            else if (lgpFile.EndsWith(".lgd", StringComparison.OrdinalIgnoreCase))
                            {
                                var lgdProgress = new Progress<(long Processed, long Total)>(p =>
                                {
                                    var pct = p.Total > 0 ? (double)p.Processed / p.Total * 100.0 : 0.0;
                                    var count = _eventLogDocs.Count;
                                    var notice = maxDocs == int.MaxValue && count >= 500_000 ? " • ⚠️ Режим «Все записи»" : "";
                                    UpdateProgress(pct, $"Записей SQLite: {p.Processed:N0}/{p.Total:N0} • Отобрано: {count:N0}{notice}");
                                });

                                await foreach (var doc in LgdParser.ParseLgdAsync(lgpFile, maxDocs, lgdProgress, token, _lgFilterDateFrom, _lgFilterDateTo).ConfigureAwait(false))
                                {
                                    if (lgFilter.HasAnyCriteria && !lgFilter.Matches(doc))
                                        continue;

                                    if (!channel.Writer.TryWrite(doc))
                                    {
                                        await channel.Writer.WriteAsync(doc, token).ConfigureAwait(false);
                                    }
                                }
                            }
                            else
                            {
                                var fileProgress = new Progress<EventLogParser.LogReadProgress>(p =>
                                {
                                    fileBytesRead[lgpFile] = p.BytesRead;
                                    fileBytesSkipped[lgpFile] = p.SkippedBytes;
                                    var now = Stopwatch.GetTimestamp();
                                    if ((now - lastUiUpdateTick) * 1000.0 / Stopwatch.Frequency >= 80)
                                    {
                                        lastUiUpdateTick = now;
                                        long curTotal = 0;
                                        foreach (var v in fileBytesRead.Values) curTotal += v;
                                        long skippedTotal = 0;
                                        foreach (var v in fileBytesSkipped.Values) skippedTotal += v;
                                        long activeRead = Math.Max(0, curTotal - skippedTotal);
                                        long targetTotal = Math.Max(1, totalBytes - skippedTotal);
                                        var count = _lgSessionCache?.TotalCount ?? _eventLogDocs.Count;
                                        var notice = maxDocs == int.MaxValue && count >= 500_000 ? " • ⚠️ Режим «Все записи» (кэш на SSD)" : "";

                                        if (skippedTotal > 0)
                                        {
                                            var pct = Math.Clamp((double)activeRead / targetTotal * 100.0, 0.0, 100.0);
                                            UpdateProgress(pct, $"⚡ Пропущено: {FormatFileSize(skippedTotal)} • Прочитано: {FormatFileSize(activeRead)} / {FormatFileSize(targetTotal)} ({pct:F1}%) • Отобрано: {count:N0}{notice}");
                                        }
                                        else
                                        {
                                            var pct = totalBytes > 0 ? (double)curTotal / totalBytes * 100.0 : 0.0;
                                            UpdateProgress(pct, $"{FormatFileSize(curTotal)} / {FormatFileSize(totalBytes)} ({pct:F1}%) • Отобрано: {count:N0}{notice}");
                                        }
                                    }
                                });

                                await foreach (var doc in EventLogParser.ParseLogAsync(lgpFile, dict, fileProgress, token, _lgFilterDateFrom, _lgFilterDateTo).ConfigureAwait(false))
                                {
                                    if (lgFilter.HasAnyCriteria && !lgFilter.Matches(doc))
                                        continue;

                                    if (!channel.Writer.TryWrite(doc))
                                    {
                                        await channel.Writer.WriteAsync(doc, token).ConfigureAwait(false);
                                    }
                                }
                            }

                            var currentDone = Interlocked.Increment(ref completedFiles);
                            fileBytesRead[lgpFile] = fileLength;
                            long doneTotal = 0;
                            foreach (var v in fileBytesRead.Values) doneTotal += v;
                            long doneSkipped = 0;
                            foreach (var v in fileBytesSkipped.Values) doneSkipped += v;
                            long doneActive = Math.Max(0, doneTotal - doneSkipped);
                            long doneTarget = Math.Max(1, totalBytes - doneSkipped);
                            var filePct = lgpFiles.Count > 0 ? (double)currentDone / lgpFiles.Count * 100.0 : 0.0;
                            var bytePct = doneTarget > 0 ? (double)doneActive / doneTarget * 100.0 : 0.0;
                            var pct = Math.Max(filePct, bytePct);
                            var countDone = _lgSessionCache?.TotalCount ?? _eventLogDocs.Count;

                            if (doneSkipped > 0)
                            {
                                UpdateProgress(pct, $"Файлов {currentDone}/{lgpFiles.Count} • ⚡ Пропущено: {FormatFileSize(doneSkipped)} • Прочитано: {FormatFileSize(doneActive)} • Отобрано: {countDone:N0}");
                            }
                            else
                            {
                                UpdateProgress(pct, $"Файлов {currentDone}/{lgpFiles.Count} ({FormatFileSize(doneTotal)} / {FormatFileSize(totalBytes)}) • Отобрано: {countDone:N0}");
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    channel.Writer.Complete();
                }
            }, ct);

            var isUnlimitedMode = maxDocs == int.MaxValue;
            const int maxUiPreview = 100_000;
            var targetUiCap = isUnlimitedMode ? maxUiPreview : maxDocs;

            var consumerTask = Task.Run(async () =>
            {
                var batch = isUnlimitedMode ? new List<EventLogDoc>(20_000) : null;
                var uiBatch = new List<EventLogDoc>(5000);
                var lastUiTick = Stopwatch.GetTimestamp();
                int uiAdded = 0;

                try
                {
                    while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        while (channel.Reader.TryRead(out var doc))
                        {
                            if (isUnlimitedMode)
                            {
                                batch!.Add(doc);
                            }

                            if (uiAdded < targetUiCap)
                            {
                                uiBatch.Add(doc);
                                uiAdded++;
                            }

                            if (!isUnlimitedMode && uiAdded >= targetUiCap)
                            {
                                _parsingCts?.Cancel();
                                break;
                            }

                            if (isUnlimitedMode && batch!.Count >= 20_000)
                            {
                                if (_lgSessionCache != null)
                                {
                                    await _lgSessionCache.InsertEventLogsAsync(batch, ct).ConfigureAwait(false);
                                }
                                batch.Clear();

                                if (uiBatch.Count > 0)
                                {
                                    var staging = uiBatch.ToArray();
                                    uiBatch.Clear();
                                    _ = Dispatcher.InvokeAsync(() =>
                                    {
                                        _eventLogDocs.AddRange(staging);
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }

                                var now = Stopwatch.GetTimestamp();
                                if ((now - lastUiTick) * 1000.0 / Stopwatch.Frequency >= 120)
                                {
                                    lastUiTick = now;
                                    _ = Dispatcher.InvokeAsync(() =>
                                    {
                                        UpdateLgCountStatus();
                                        UpdateTempSizeBadge();
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }
                            }
                            else if (!isUnlimitedMode && uiBatch.Count >= 5000)
                            {
                                var staging = uiBatch.ToArray();
                                uiBatch.Clear();
                                _ = Dispatcher.InvokeAsync(() =>
                                {
                                    _eventLogDocs.AddRange(staging);
                                    UpdateLgCountStatus();
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }

                        if (!isUnlimitedMode && uiAdded >= targetUiCap)
                        {
                            break;
                        }

                        if (isUnlimitedMode && batch != null && batch.Count > 0)
                        {
                            var now = Stopwatch.GetTimestamp();
                            if ((now - lastUiTick) * 1000.0 / Stopwatch.Frequency >= 250)
                            {
                                if (_lgSessionCache != null)
                                {
                                    await _lgSessionCache.InsertEventLogsAsync(batch, ct).ConfigureAwait(false);
                                }
                                batch.Clear();

                                if (uiBatch.Count > 0)
                                {
                                    var staging = uiBatch.ToArray();
                                    uiBatch.Clear();
                                    _ = Dispatcher.InvokeAsync(() =>
                                    {
                                        _eventLogDocs.AddRange(staging);
                                    }, System.Windows.Threading.DispatcherPriority.Background);
                                }

                                lastUiTick = now;
                                _ = Dispatcher.InvokeAsync(() =>
                                {
                                    UpdateLgCountStatus();
                                    UpdateTempSizeBadge();
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                        }
                    }

                    if (isUnlimitedMode && batch != null && batch.Count > 0)
                    {
                        if (_lgSessionCache != null)
                        {
                            await _lgSessionCache.InsertEventLogsAsync(batch, ct).ConfigureAwait(false);
                        }
                        batch.Clear();
                    }

                    if (uiBatch.Count > 0)
                    {
                        var staging = uiBatch.ToArray();
                        uiBatch.Clear();
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _eventLogDocs.AddRange(staging);
                            UpdateLgCountStatus();
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);

            try
            {
                await Task.WhenAll(workerTask, consumerTask);
            }
            catch (OperationCanceledException) { }

            if (_lgSessionCache != null && _lgSessionCache.TotalCount > 0)
            {
                try
                {
                    await _lgSessionCache.EnsureIndexesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
            }

            UpdateTempSizeBadge();
            var totalLgCount = (_lgSessionCache != null && _lgSessionCache.TotalCount > 0) ? _lgSessionCache.TotalCount : _eventLogDocs.Count;
            long totalSkipped = 0;
            foreach (var v in fileBytesSkipped.Values) totalSkipped += v;
            var skippedInfo = totalSkipped > 0 ? $" (пропущено {FormatFileSize(totalSkipped)} предшествующих дней)" : "";

            if (totalLgCount > _eventLogDocs.Count)
            {
                TxtStatus.Text = $"Разбор ЖР завершен. Всего записей: {totalLgCount:N0} (кэш на SSD){skippedInfo}. В таблице отображаются первые {_eventLogDocs.Count:N0} записей.";
            }
            else
            {
                TxtStatus.Text = $"Разбор ЖР завершен. Загружено {totalLgCount:N0} записей{skippedInfo}.";
            }

            if (_eventLogDocs.Count > 100_000)
            {
                TxtStatus.Text = $"Подготовка таблицы и фасетных фильтров для {_eventLogDocs.Count:N0} записей...";
                await Task.Yield();
            }

            // Сортировка по активным колонкам (Direct In-Memory Sort)
            if (_activeLgSorts.Count > 0)
            {
                if (_eventLogDocs.Count > 100_000)
                    await Task.Run(() => SortEventLogDocs(_eventLogDocs, _activeLgSorts));
                else
                    SortEventLogDocs(_eventLogDocs, _activeLgSorts);
            }
            else
            {
                ApplyLgSortPreset();
            }

            PopulateLgEventFilterItems();
            PopulateLgExcludeFilterItems();
            await PopulateLgFieldValuesAsync();
            _currentLgSnapshot = BuildLgFilterSnapshot();
            _lgCollectionView = CollectionViewSource.GetDefaultView(_eventLogDocs);
            _lgCollectionView.Filter = FilterLgDoc;

            foreach (var col in GridLg.Columns)
            {
                var match = _activeLgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }

            GridLg.ItemsSource = _lgCollectionView;
            UpdateLgFilterChips();
            UpdateLgCountStatus();
            if (_eventLogDocs.Count > 0)
            {
                GridLg.SelectedIndex = 0;
            }

            if (ct.IsCancellationRequested)
            {
                TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем! Загружено {_eventLogDocs.Count:N0} записей.";
            }
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = $"⚠️ Парсинг остановлен пользователем. Загружено {_eventLogDocs.Count} записей.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при парсинге ЖР: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка парсинга ЖР.";
        }
        finally
        {
            HideProgress();
            BtnParseLg.IsEnabled = true;
            BtnCancelLg.Visibility = Visibility.Collapsed;
            DeepGarbageCollection();
            _parsingCts?.Dispose();
            _parsingCts = null;
        }
    }

    /// <summary>
    /// Ручной сброс и глубокая оптимизация оперативной памяти (LOH/Gen2 + Win32 WorkingSet Trim).
    /// </summary>
    private void BtnCleanMemory_Click(object sender, RoutedEventArgs e)
    {
        var beforeMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
        DeepGarbageCollection();
        UpdateTempSizeBadge();
        var afterMb = GC.GetTotalMemory(true) / 1024.0 / 1024.0;
        TxtStatus.Text = $"🧹 Память оптимизирована и освобождена ({beforeMb:F1} МБ -> {afterMb:F1} МБ).";
    }

    /// <summary>
    /// Переход в Telegram автора приложения (@DarkSailas).
    /// </summary>
    private void TgLink_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://t.me/DarkSailas",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть Telegram ссылку @DarkSailas");
        }
    }

    /// <summary>
    /// Разовая отправка распарсенных (или выбранных) записей ТЖ в Elasticsearch с диалоговым подтверждением.
    /// </summary>
    private async void BtnExportTgElastic_Click(object sender, RoutedEventArgs e)
    {
        var selectedDocs = GridTg.SelectedItems.OfType<TechLogDoc>().ToList();
        var isSelectedScope = selectedDocs.Count > 0;
        var docsToSend = isSelectedScope ? selectedDocs : _techLogDocs;

        if (docsToSend.Count == 0)
        {
            MessageBox.Show("Сначала распарсьте Технологический Журнал (ТЖ) или выберите записи в таблице!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildElasticSettingsFromUi();
        var indexName = $"{settings.TechLogIndexPrefix}_gui_{DateTime.UtcNow:yyyyMMdd}";
        var scopeDescription = isSelectedScope ? $"выделенные {docsToSend.Count} записи(ей)" : $"все {docsToSend.Count} записи(ей) таблицы";

        var confirmResult = MessageBox.Show(
            $"Вы уверены, что хотите отправить {scopeDescription} Технологического Журнала в Elasticsearch / Kibana?\n\n" +
            $"• Сервер назначения: {settings.ServerUrl}\n" +
            $"• Целевой индекс: {indexName}\n" +
            $"• Размер пачки (Bulk): {settings.BulkBatchSize} документов",
            "Подтверждение отправки в Elastic",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            TxtStatus.Text = "Отправка ТЖ отменена пользователем.";
            return;
        }

        BtnExportTgElastic.IsEnabled = false;
        TxtStatus.Text = $"Идет отправка {docsToSend.Count} записей ТЖ в Elasticsearch...";

        try
        {
            var publisher = new ElasticPublisher(settings, NullLogger<ElasticPublisher>.Instance);
            var (success, failed) = await publisher.BulkIndexTechLogAsync(indexName, docsToSend);

            var scopeText = isSelectedScope ? "выделенных" : "всех";
            MessageBox.Show($"Отправка записей ТЖ завершена!\n\n• Индекс: {indexName}\n• Успешно отправлено: {success} записей ({scopeText})\n• Сбоев: {failed}", "Отправка в Elastic", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"Успешно отправлено {success} записей ТЖ в индекс '{indexName}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при отправке в Elasticsearch:\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка отправки в Elastic.";
        }
        finally
        {
            BtnExportTgElastic.IsEnabled = true;
        }
    }

    /// <summary>
    /// Разовая отправка распарсенных (или выбранных) записей ТЖ в ClickHouse с диалоговым подтверждением.
    /// </summary>
    private async void BtnExportTgClickHouse_Click(object sender, RoutedEventArgs e)
    {
        var selectedDocs = GridTg.SelectedItems.OfType<TechLogDoc>().ToList();
        var isSelectedScope = selectedDocs.Count > 0;
        var docsToSend = isSelectedScope ? selectedDocs : _techLogDocs;

        if (docsToSend.Count == 0)
        {
            MessageBox.Show("Сначала распарсьте Технологический Журнал (ТЖ) или выберите записи в таблице!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildClickHouseSettingsFromUi();
        var scopeDescription = isSelectedScope ? $"выделенные {docsToSend.Count} записи(ей)" : $"все {docsToSend.Count} записи(ей) таблицы";

        var confirmResult = MessageBox.Show(
            $"Вы уверены, что хотите отправить {scopeDescription} Технологического Журнала в ClickHouse?\n\n" +
            $"• Сервер назначения: {settings.ServerUrl}\n" +
            $"• База данных: {settings.Database}\n" +
            $"• Целевая таблица: {settings.TechLogTable}\n" +
            $"• Размер пачки (Bulk): {settings.BulkBatchSize} документов",
            "Подтверждение отправки в ClickHouse",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            TxtStatus.Text = "Отправка ТЖ в ClickHouse отменена пользователем.";
            return;
        }

        BtnExportTgClickHouse.IsEnabled = false;
        TxtStatus.Text = $"Идет отправка {docsToSend.Count} записей ТЖ в ClickHouse ({settings.Database}.{settings.TechLogTable})...";

        try
        {
            using var publisher = new ClickHousePublisher(settings, NullLogger<ClickHousePublisher>.Instance);
            var (success, failed) = await publisher.BulkInsertTechLogAsync(docsToSend);

            var scopeText = isSelectedScope ? "выделенных" : "всех";
            MessageBox.Show($"Отправка записей ТЖ в ClickHouse завершена!\n\n• Таблица: {settings.Database}.{settings.TechLogTable}\n• Успешно отправлено: {success} записей ({scopeText})\n• Сбоев: {failed}", "Отправка в ClickHouse", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"Успешно отправлено {success} записей ТЖ в ClickHouse '{settings.Database}.{settings.TechLogTable}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при отправке в ClickHouse:\n{ex.Message}", "Ошибка подключения ClickHouse", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка отправки в ClickHouse.";
        }
        finally
        {
            BtnExportTgClickHouse.IsEnabled = true;
        }
    }

    /// <summary>
    /// Разовая отправка распарсенных (или выбранных) записей ЖР в Elasticsearch с диалоговым подтверждением.
    /// </summary>
    private async void BtnExportLgElastic_Click(object sender, RoutedEventArgs e)
    {
        var selectedDocs = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        var isSelectedScope = selectedDocs.Count > 0;
        var docsToSend = isSelectedScope ? selectedDocs : _eventLogDocs;

        if (docsToSend.Count == 0)
        {
            MessageBox.Show("Сначала распарсьте Журнал Регистрации (ЖР) или выберите записи в таблице!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildElasticSettingsFromUi();
        var indexName = $"{settings.EventLogIndexPrefix}_gui_{DateTime.UtcNow:yyyyMMdd}";
        var scopeDescription = isSelectedScope ? $"выделенные {docsToSend.Count} записи(ей)" : $"все {docsToSend.Count} записи(ей) таблицы";

        var confirmResult = MessageBox.Show(
            $"Вы уверены, что хотите отправить {scopeDescription} Журнала Регистрации в Elasticsearch / Kibana?\n\n" +
            $"• Сервер назначения: {settings.ServerUrl}\n" +
            $"• Целевой индекс: {indexName}\n" +
            $"• Размер пачки (Bulk): {settings.BulkBatchSize} документов",
            "Подтверждение отправки в Elastic",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            TxtStatus.Text = "Отправка ЖР отменена пользователем.";
            return;
        }

        BtnExportLgElastic.IsEnabled = false;
        TxtStatus.Text = $"Идет отправка {docsToSend.Count} записей ЖР в Elasticsearch...";

        try
        {
            var publisher = new ElasticPublisher(settings, NullLogger<ElasticPublisher>.Instance);
            var (success, failed) = await publisher.BulkIndexEventLogAsync(indexName, docsToSend);

            var scopeText = isSelectedScope ? "выделенных" : "всех";
            MessageBox.Show($"Отправка записей ЖР завершена!\n\n• Индекс: {indexName}\n• Успешно отправлено: {success} записей ({scopeText})\n• Сбоев: {failed}", "Отправка в Elastic", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"Успешно отправлено {success} записей ЖР в индекс '{indexName}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при отправке в Elasticsearch:\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка отправки в Elastic.";
        }
        finally
        {
            BtnExportLgElastic.IsEnabled = true;
        }
    }

    /// <summary>
    /// Разовая отправка распарсенных (или выбранных) записей ЖР в ClickHouse с диалоговым подтверждением.
    /// </summary>
    private async void BtnExportLgClickHouse_Click(object sender, RoutedEventArgs e)
    {
        var selectedDocs = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        var isSelectedScope = selectedDocs.Count > 0;
        var docsToSend = isSelectedScope ? selectedDocs : _eventLogDocs;

        if (docsToSend.Count == 0)
        {
            MessageBox.Show("Сначала распарсьте Журнал Регистрации (ЖР) или выберите записи в таблице!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildClickHouseSettingsFromUi();
        var scopeDescription = isSelectedScope ? $"выделенные {docsToSend.Count} записи(ей)" : $"все {docsToSend.Count} записи(ей) таблицы";

        var confirmResult = MessageBox.Show(
            $"Вы уверены, что хотите отправить {scopeDescription} Журнала Регистрации в ClickHouse?\n\n" +
            $"• Сервер назначения: {settings.ServerUrl}\n" +
            $"• База данных: {settings.Database}\n" +
            $"• Целевая таблица: {settings.EventLogTable}\n" +
            $"• Размер пачки (Bulk): {settings.BulkBatchSize} документов",
            "Подтверждение отправки в ClickHouse",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes)
        {
            TxtStatus.Text = "Отправка ЖР в ClickHouse отменена пользователем.";
            return;
        }

        BtnExportLgClickHouse.IsEnabled = false;
        TxtStatus.Text = $"Идет отправка {docsToSend.Count} записей ЖР в ClickHouse ({settings.Database}.{settings.EventLogTable})...";

        try
        {
            using var publisher = new ClickHousePublisher(settings, NullLogger<ClickHousePublisher>.Instance);
            var (success, failed) = await publisher.BulkInsertEventLogAsync(docsToSend);

            var scopeText = isSelectedScope ? "выделенных" : "всех";
            MessageBox.Show($"Отправка записей ЖР в ClickHouse завершена!\n\n• Таблица: {settings.Database}.{settings.EventLogTable}\n• Успешно отправлено: {success} записей ({scopeText})\n• Сбоев: {failed}", "Отправка в ClickHouse", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = $"Успешно отправлено {success} записей ЖР в ClickHouse '{settings.Database}.{settings.EventLogTable}'.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при отправке в ClickHouse:\n{ex.Message}", "Ошибка подключения ClickHouse", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка отправки в ClickHouse.";
        }
        finally
        {
            BtnExportLgClickHouse.IsEnabled = true;
        }
    }

    /// <summary>
    /// Отображение выбранной строки ТЖ в виде форматированного JSON.
    /// </summary>
    private void GridTg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void GridLg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    /// <summary>
    /// Открытие веб-интерфейса Kibana в системном браузере.
    /// </summary>
    private void BtnOpenKibana_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtKibanaUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://localhost:5601";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            TxtStatus.Text = $"Открываем веб-интерфейс Kibana: {url}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось открыть браузер: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Проверка сетевой доступности сервера Kibana по HTTP.
    /// </summary>
    private async void BtnTestKibana_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtKibanaUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://localhost:5601";

        TxtStatus.Text = $"Проверка соединения с Kibana по адресу {url}...";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                MessageBox.Show($"Соединение с Kibana успешно установлено!\n\nСтатус: {(int)response.StatusCode} {response.ReasonPhrase}", "Успешное подключение", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Подключение к Kibana подтверждено!";
            }
            else
            {
                MessageBox.Show($"Сервер Kibana ответил кодом: {(int)response.StatusCode} {response.ReasonPhrase}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtStatus.Text = "Сервер Kibana вернул предупреждение.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка соединения с Kibana ({url}):\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка подключения к Kibana.";
        }
    }

    /// <summary>
    /// Проверка сетевого соединения с Elasticsearch / OpenSearch по HTTP.
    /// </summary>
    private async void BtnTestElastic_Click(object sender, RoutedEventArgs e)
    {
        var url = TxtElasticUrl.Text.Trim();
        if (string.IsNullOrEmpty(url)) url = "http://localhost:9200";

        var user = TxtElasticUser.Text.Trim();
        var pass = TxtElasticPass.Password.Trim();
        var apiKey = TxtElasticApiKey.Text.Trim();

        TxtStatus.Text = $"Проверка соединения с Elasticsearch по адресу {url}...";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
            {
                var authBytes = Encoding.UTF8.GetBytes($"{user}:{pass}");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            }
            else if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", apiKey);
            }

            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var authInfo = !string.IsNullOrEmpty(user) ? $" (Авторизован как {user})" : " (Без авторизации)";
                MessageBox.Show($"Соединение с Elasticsearch / OpenSearch успешно установлено!{authInfo}\n\nОтвет сервера:\n{content[..Math.Min(250, content.Length)]}...", "Успешное подключение", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Подключение к Elastic/OpenSearch подтверждено!";
            }
            else
            {
                MessageBox.Show($"Сервер ответил кодом: {(int)response.StatusCode} {response.ReasonPhrase}", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtStatus.Text = "Сервер Elastic вернул ошибки авторизации или доступа.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка соединения с Elasticsearch ({url}):\n{ex.Message}", "Ошибка подключения", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка подключения к Elastic.";
        }
    }

    /// <summary>
    /// Проверка сетевого соединения и аутентификации в ClickHouse по HTTP (SELECT version()).
    /// </summary>
    private async void BtnTestClickHouse_Click(object sender, RoutedEventArgs e)
    {
        var settings = BuildClickHouseSettingsFromUi();
        TxtStatus.Text = $"Проверка соединения с ClickHouse по адресу {settings.ServerUrl}...";

        try
        {
            using var publisher = new ClickHousePublisher(settings, NullLogger<ClickHousePublisher>.Instance);
            var (success, message) = await publisher.TestConnectionAsync();

            if (success)
            {
                MessageBox.Show(message, "Успешное подключение к ClickHouse", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtStatus.Text = "Подключение к ClickHouse подтверждено!";
            }
            else
            {
                MessageBox.Show(message, "Предупреждение ClickHouse", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtStatus.Text = "ClickHouse вернул ошибку проверки связи.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка соединения с ClickHouse ({settings.ServerUrl}):\n{ex.Message}", "Ошибка подключения ClickHouse", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Ошибка подключения к ClickHouse.";
        }
    }

    /// <summary>
    /// Сохранение измененных параметров (Elasticsearch, Kibana, ClickHouse, FileDump ротация) в appsettings.json.
    /// </summary>
    private async void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(configPath))
            {
                MessageBox.Show("Файл appsettings.json не найден в рабочей директории!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var jsonString = await File.ReadAllTextAsync(configPath);
            var rootNode = JsonNode.Parse(jsonString, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (rootNode is JsonObject rootObj)
            {
                var exporterObj = rootObj["Exporter"] as JsonObject ?? rootObj;

                if (exporterObj["TechLog"] is JsonObject tgObj)
                {
                    tgObj["DirectoryPath"] = TxtTgPath.Text.Trim();
                    if (ChkTgLoadArchive != null) tgObj["LoadArchive"] = ChkTgLoadArchive.IsChecked ?? false;
                }

                if (exporterObj["EventLog"] is JsonObject evObj)
                {
                    evObj["DirectoryPath"] = TxtLgPath.Text.Trim();
                    if (ChkLgLoadArchive != null) evObj["LoadArchive"] = ChkLgLoadArchive.IsChecked ?? false;
                }

                if (exporterObj["Elastic"] is JsonObject elasticObj)
                {
                    elasticObj.Remove("Enabled");
                    var isElChecked = ChkElasticEnabled.IsChecked ?? false;
                    if (!isElChecked)
                    {
                        elasticObj["TechLogEnabled"] = false;
                        elasticObj["EventLogEnabled"] = false;
                    }
                    else if (!elasticObj.ContainsKey("TechLogEnabled") && !elasticObj.ContainsKey("EventLogEnabled"))
                    {
                        elasticObj["TechLogEnabled"] = false;
                        elasticObj["EventLogEnabled"] = true;
                    }
                    elasticObj["ServerUrl"] = TxtElasticUrl.Text.Trim();
                    elasticObj["Username"] = TxtElasticUser.Text.Trim();
                    elasticObj["Password"] = TxtElasticPass.Password.Trim();
                    elasticObj["ApiKey"] = TxtElasticApiKey.Text.Trim();
                    elasticObj["EventLogIndexPrefix"] = TxtEventLogPrefix.Text.Trim();
                    elasticObj["TechLogIndexPrefix"] = TxtTechLogPrefix.Text.Trim();
                }

                if (exporterObj["Kibana"] is JsonObject kibanaObj)
                {
                    kibanaObj["ServerUrl"] = TxtKibanaUrl.Text.Trim();
                }

                var chObj = exporterObj["ClickHouse"] as JsonObject;
                if (chObj == null)
                {
                    chObj = new JsonObject();
                    exporterObj["ClickHouse"] = chObj;
                }
                chObj.Remove("Enabled");
                var isChChecked = ChkClickHouseEnabled.IsChecked ?? false;
                if (!isChChecked)
                {
                    chObj["TechLogEnabled"] = false;
                    chObj["EventLogEnabled"] = false;
                }
                else if (!chObj.ContainsKey("TechLogEnabled") && !chObj.ContainsKey("EventLogEnabled"))
                {
                    chObj["TechLogEnabled"] = false;
                    chObj["EventLogEnabled"] = true;
                }
                chObj["ServerUrl"] = TxtClickHouseUrl.Text.Trim();
                chObj["Database"] = TxtClickHouseDb.Text.Trim();
                chObj["User"] = TxtClickHouseUser.Text.Trim();
                chObj["Password"] = TxtClickHousePass.Password.Trim();
                chObj["TechLogTable"] = TxtClickHouseTechLogTable.Text.Trim();
                chObj["EventLogTable"] = TxtClickHouseEventLogTable.Text.Trim();
                if (int.TryParse(TxtClickHouseBatchSize.Text.Trim(), out var bsz))
                {
                    chObj["BulkBatchSize"] = bsz;
                }

                if (exporterObj["FileDump"] is JsonObject dumpObj)
                {
                    dumpObj.Remove("DirectoryPath");
                    dumpObj["TechLogDirectoryPath"] = TxtDumpDirectoryTechLog.Text.Trim();
                    dumpObj["EventLogDirectoryPath"] = TxtDumpDirectoryEventLog.Text.Trim();
                    if (int.TryParse(TxtDumpMaxTotalSizeMb.Text.Trim(), out var mts))
                    {
                        dumpObj["MaxTotalSizeMb"] = mts;
                    }
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                };

                await File.WriteAllTextAsync(configPath, rootObj.ToJsonString(options), Encoding.UTF8);
            }

            SaveGuiState();

            MessageBox.Show("Конфигурация (Elasticsearch, Kibana, ClickHouse, FileDump) и фильтры программы успешно сохранены!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            TxtStatus.Text = "Настройки и фильтры сохранены.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при сохранении конфигурации: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Экспорт выбранных строк или всей таблицы Технологического Журнала (ТЖ) в JSON / JSONL.
    /// </summary>
    private async void BtnExportTgJson_Click(object sender, RoutedEventArgs e)
    {
        await ExportTgDocsToJsonAsync();
    }

    /// <summary>
    /// Экспорт выбранных строк или всей таблицы Журнала Регистрации (ЖР) в JSON / JSONL.
    /// </summary>
    private async void BtnExportLgJson_Click(object sender, RoutedEventArgs e)
    {
        await ExportLgDocsToJsonAsync();
    }

    private async Task ExportTgDocsToJsonAsync()
    {
        var selectedDocs = GridTg.SelectedItems.OfType<TechLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_tgCollectionView?.Cast<TechLogDoc>().ToList() ?? _techLogDocs);

        var totalCache = _tgSessionCache?.TotalCount ?? 0;
        if (docsToExport.Count == 0 && totalCache == 0)
        {
            MessageBox.Show("Нет данных Технологического Журнала (ТЖ) для экспорта! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var canStreamFullCache = !isSelected && totalCache > docsToExport.Count;
        var countForTitle = canStreamFullCache ? totalCache : docsToExport.Count;
        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : (canStreamFullCache ? $"full_{totalCache}" : "table");
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ТЖ в JSON" : $"Экспорт ТЖ ({countForTitle:N0} строк) в JSON",
            Filter = "JSON Lines файлы (*.jsonl)|*.jsonl|Форматированный JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = $"ones_techlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var isJsonLines = saveDialog.FileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                if (isJsonLines && canStreamFullCache && _tgSessionCache != null)
                {
                    var compactOptions = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };
                    await using var fs = new FileStream(saveDialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                    await using var writer = new StreamWriter(fs, Encoding.UTF8);
                    long exportedCount = 0;
                    await foreach (var doc in _tgSessionCache.StreamAllTechLogsAsync().ConfigureAwait(false))
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(doc, compactOptions)).ConfigureAwait(false);
                        exportedCount++;
                    }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show($"Экспорт ТЖ успешно завершен!\n\n• Сохранены: все {exportedCount:N0} записей из сессионного кэша\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtStatus.Text = $"Экспортировано {exportedCount:N0} записей ТЖ в JSON (потоково из кэша).";
                    });
                }
                else if (isJsonLines)
                {
                    var compactOptions = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };
                    var sb = new StringBuilder();
                    foreach (var doc in docsToExport)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(doc, compactOptions));
                    }
                    await File.WriteAllTextAsync(saveDialog.FileName, sb.ToString(), Encoding.UTF8);

                    var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                    MessageBox.Show($"Экспорт ТЖ успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                    TxtStatus.Text = $"Экспортировано {docsToExport.Count} записей ТЖ в JSON.";
                }
                else
                {
                    var json = JsonSerializer.Serialize(docsToExport, PrettyJson);
                    await File.WriteAllTextAsync(saveDialog.FileName, json, Encoding.UTF8);

                    var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                    MessageBox.Show($"Экспорт ТЖ успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                    TxtStatus.Text = $"Экспортировано {docsToExport.Count} записей ТЖ в JSON.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при экспорте ТЖ в JSON:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task ExportLgDocsToJsonAsync()
    {
        var selectedDocs = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_lgCollectionView?.Cast<EventLogDoc>().ToList() ?? _eventLogDocs);

        var totalCache = _lgSessionCache?.TotalCount ?? 0;
        if (docsToExport.Count == 0 && totalCache == 0)
        {
            MessageBox.Show("Нет данных Журнала Регистрации (ЖР) для экспорта! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var canStreamFullCache = !isSelected && totalCache > docsToExport.Count;
        var countForTitle = canStreamFullCache ? totalCache : docsToExport.Count;
        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : (canStreamFullCache ? $"full_{totalCache}" : "table");
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ЖР в JSON" : $"Экспорт ЖР ({countForTitle:N0} строк) в JSON",
            Filter = "JSON Lines файлы (*.jsonl)|*.jsonl|Форматированный JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = $"ones_eventlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"
        };

        if (saveDialog.ShowDialog() == true)
        {
            try
            {
                var isJsonLines = saveDialog.FileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
                if (isJsonLines && canStreamFullCache && _lgSessionCache != null)
                {
                    var compactOptions = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };
                    await using var fs = new FileStream(saveDialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                    await using var writer = new StreamWriter(fs, Encoding.UTF8);
                    long exportedCount = 0;
                    await foreach (var doc in _lgSessionCache.StreamAllEventLogsAsync().ConfigureAwait(false))
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(doc, compactOptions)).ConfigureAwait(false);
                        exportedCount++;
                    }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show($"Экспорт ЖР успешно завершен!\n\n• Сохранены: все {exportedCount:N0} записей из сессионного кэша\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                        TxtStatus.Text = $"Экспортировано {exportedCount:N0} записей ЖР в JSON (потоково из кэша).";
                    });
                }
                else if (isJsonLines)
                {
                    var compactOptions = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };
                    var sb = new StringBuilder();
                    foreach (var doc in docsToExport)
                    {
                        sb.AppendLine(JsonSerializer.Serialize(doc, compactOptions));
                    }
                    await File.WriteAllTextAsync(saveDialog.FileName, sb.ToString(), Encoding.UTF8);

                    var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                    MessageBox.Show($"Экспорт ЖР успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                    TxtStatus.Text = $"Экспортировано {docsToExport.Count} записей ЖР в JSON.";
                }
                else
                {
                    var json = JsonSerializer.Serialize(docsToExport, PrettyJson);
                    await File.WriteAllTextAsync(saveDialog.FileName, json, Encoding.UTF8);

                    var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                    MessageBox.Show($"Экспорт ЖР успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {saveDialog.FileName}", "Экспорт в JSON", MessageBoxButton.OK, MessageBoxImage.Information);
                    TxtStatus.Text = $"Экспортировано {docsToExport.Count} записей ЖР в JSON.";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при экспорте ЖР в JSON:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Отображение модального окна профиля события (Dribbble Profile Information Popup).
    /// </summary>
    private void ShowDocDetails(object? docObj)
    {
        if (docObj == null) return;

        string title = "Детализация события 1С";
        string subTitle = "";
        string jsonText = "";
        string contextText = "";
        List<KeyValuePair<string, string>> propsList = [];

        if (docObj is TechLogDoc tg)
        {
            var longInfo = !string.IsNullOrEmpty(tg.LongInfoName) ? $" [Действие: {tg.LongInfoName}]" : "";
            title = $"Событие ТЖ: {tg.Event}{longInfo}";
            subTitle = $"{tg.DateFormatted} | Статус: {tg.ExecutionStatus} | Процесс: {tg.ProcessName} (PID {tg.ProcessId}, OSThread {tg.OSThread}) | Длительность: {tg.DurationFormatted}";
            jsonText = JsonSerializer.Serialize(tg, PrettyJson);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(tg.LongInfoName)) sb.AppendLine($"=== ЦЕЛЕВОЕ ДЕЙСТВИЕ (LONGDURATIONINFO) ===\n{tg.LongInfoName} (Текущее ожидание: {tg.LongInfoWait} мкс)\n");
            if (!string.IsNullOrWhiteSpace(tg.Context)) sb.AppendLine($"=== КОНТЕКСТ ===\n{tg.Context}\n");
            if (!string.IsNullOrWhiteSpace(tg.Sql)) sb.AppendLine($"=== SQL ЗАПРОС ===\n{tg.Sql}\n");
            if (!string.IsNullOrWhiteSpace(tg.Locks)) sb.AppendLine($"=== БЛОКИРОВКИ ===\n{tg.Locks}\n");
            if (!string.IsNullOrWhiteSpace(tg.Descr)) sb.AppendLine($"=== ОПИСАНИЕ / ОШИБКА ===\n{tg.Descr}\n");
            if (!string.IsNullOrWhiteSpace(tg.WaitConnections)) sb.AppendLine($"=== ОЖИДАНИЕ СОЕДИНЕНИЙ ===\n{tg.WaitConnections}\n");
            contextText = sb.Length > 0 ? sb.ToString() : "(Дополнительный контекст отсутствует)";

            propsList =
            [
                new("Событие (Event)", tg.Event),
                new("Статус операции", tg.ExecutionStatus),
                new("Дата и время", tg.DateFormatted),
                new("Длительность", tg.DurationFormatted),
                new("Процесс 1С", tg.ProcessName ?? ""),
                new("PID процесса (Диспетчер задач Windows)", tg.ProcessId ?? ""),
                new("SPID СУБД (Соединение в MS SQL / PostgreSQL)", !string.IsNullOrEmpty(tg.Spid) ? tg.Spid : "— (СУБД не задействована)"),
                new("Поток ОС (OSThread)", tg.OSThread ?? ""),
                new("Действие (LongInfoName)", tg.LongInfoName ?? ""),
                new("Пользователь (Usr)", tg.User ?? ""),
                new("Приложение (App)", tg.App ?? ""),
                new("Сеанс 1С (SessionID)", tg.SessionId ?? ""),
                new("ID соединения 1С (ConnectID)", tg.ConnectId ?? "")
            ];

            foreach (var (k, v) in tg.Properties)
            {
                propsList.Add(new(k, v));
            }
        }
        else if (docObj is EventLogDoc lg)
        {
            title = $"Событие ЖР: {lg.Event}";
            var fileInfo = !string.IsNullOrEmpty(lg.FileName) ? $" | Файл: {lg.FileName} ({lg.FileSizeFormatted})" : "";
            var compInfo = !string.IsNullOrEmpty(lg.Computer) ? $" | ПК: {lg.Computer}" : "";
            subTitle = $"{lg.DateFormatted} | Важность: {lg.Importance} | Пользователь: {lg.User} | Мета: {lg.Meta}{compInfo}{fileInfo}";
            jsonText = JsonSerializer.Serialize(lg, PrettyJson);
            contextText = string.IsNullOrWhiteSpace(lg.Comment) ? "(Комментарий отсутствует)" : lg.Comment;

            propsList =
            [
                new("Событие (Event)", lg.Event ?? "—"),
                new("Дата и время", lg.DateFormatted),
                new("Важность (Importance)", lg.Importance ?? "—"),
                new("Пользователь (User)", string.IsNullOrEmpty(lg.User) ? "—" : lg.User),
                new("Компьютер (Computer)", string.IsNullOrEmpty(lg.Computer) ? "—" : lg.Computer),
                new("Приложение (App)", string.IsNullOrEmpty(lg.App) ? "—" : lg.App),
                new("Тип клиента (AppType)", string.IsNullOrEmpty(lg.AppTypeName) ? "—" : lg.AppTypeName),
                new("Метаданные (Meta)", string.IsNullOrEmpty(lg.Meta) ? "—" : lg.Meta),
                new("Представление данных", string.IsNullOrEmpty(lg.DataPresentation) ? "— (не задано)" : lg.DataPresentation),
                new("Данные (Data)", string.IsNullOrEmpty(lg.Data) ? "—" : lg.Data),
                new("Сеанс (Session)", string.IsNullOrEmpty(lg.Session) ? "—" : lg.Session),
                new("Соединение (Connection)", string.IsNullOrEmpty(lg.Connection) ? "—" : lg.Connection),
                new("Сервер кластера", string.IsNullOrEmpty(lg.Server) ? "—" : lg.Server),
                new("Порт процесса", string.IsNullOrEmpty(lg.Port) ? "—" : lg.Port),
                new("Транзакция (Tran)", string.IsNullOrEmpty(lg.Tran) ? "—" : lg.Tran),
                new("Статус транзакции", string.IsNullOrEmpty(lg.TranStatusText) ? "—" : lg.TranStatusText),
                new("Комментарий", string.IsNullOrEmpty(lg.Comment) ? "—" : lg.Comment),
                new("Файл (FileName)", string.IsNullOrEmpty(lg.FileName) ? "—" : lg.FileName),
                new("Размер файла (FileSize)", string.IsNullOrEmpty(lg.FileSizeFormatted) ? "—" : lg.FileSizeFormatted)
            ];
        }

        TxtModalTitle.Text = title;
        TxtModalSubTitle.Text = subTitle;
        TxtModalJson.Text = jsonText;
        TxtModalContext.Text = contextText;
        GridModalProps.ItemsSource = propsList;

        OverlayDetails.Visibility = Visibility.Visible;
    }

    private void BtnInspectRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext != null)
        {
            ShowDocDetails(btn.DataContext);
        }
    }

    private void GridTg_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShowDocDetails(GridTg.SelectedItem);
    }

    private void GridLg_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShowDocDetails(GridLg.SelectedItem);
    }

    private void BtnCloseModal_Click(object sender, RoutedEventArgs e)
    {
        OverlayDetails.Visibility = Visibility.Collapsed;
    }

    private void OverlayDetails_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource == OverlayDetails)
        {
            OverlayDetails.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnCopyJson_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtModalJson.Text))
        {
            Clipboard.SetText(TxtModalJson.Text);
            TxtStatus.Text = "📋 Форматированный JSON скопирован в буфер обмена.";
        }
    }

    private void BtnCopyModalContext_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtModalContext.Text))
        {
            Clipboard.SetText(TxtModalContext.Text);
            TxtStatus.Text = "🔍 Контекст и SQL запрос скопированы в буфер обмена.";
        }
    }

    private void BtnCopyModalAll_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {TxtModalTitle.Text} ===");
        sb.AppendLine(TxtModalSubTitle.Text);
        sb.AppendLine();
        sb.AppendLine("--- СВОЙСТВА СОБЫТИЯ ---");
        if (GridModalProps.ItemsSource is IEnumerable<KeyValuePair<string, string>> props)
        {
            foreach (var kv in props)
            {
                sb.AppendLine($"{kv.Key}: {kv.Value}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("--- КОНТЕКСТ / SQL ---");
        sb.AppendLine(TxtModalContext.Text);

        Clipboard.SetText(sb.ToString());
        TxtStatus.Text = "📑 Все данные и свойства события скопированы в буфер обмена.";
    }

    private string _lastClickedPropCellText = "";
    private KeyValuePair<string, string>? _lastClickedPropPair;

    private void GridModalProps_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cell = FindVisualParent<DataGridCell>(dep);
            if (cell != null)
            {
                if (cell.Content is TextBlock tb)
                {
                    _lastClickedPropCellText = tb.Text;
                }
            }

            var row = FindVisualParent<DataGridRow>(dep);
            if (row != null && row.Item is KeyValuePair<string, string> kv)
            {
                _lastClickedPropPair = kv;
                if (!GridModalProps.SelectedItems.Contains(kv))
                {
                    GridModalProps.SelectedItem = kv;
                }
            }
        }
    }

    private void GridModalProps_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyModalPropsSelection();
            e.Handled = true;
        }
    }

    private void ModalPropsMenuCopyVal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClickedPropPair.HasValue)
        {
            Clipboard.SetText(_lastClickedPropPair.Value.Value);
            TxtStatus.Text = $"📋 Скопировано значение: {_lastClickedPropPair.Value.Value}";
        }
        else if (!string.IsNullOrEmpty(_lastClickedPropCellText))
        {
            Clipboard.SetText(_lastClickedPropCellText);
            TxtStatus.Text = $"📋 Скопировано значение: {_lastClickedPropCellText}";
        }
    }

    private void ModalPropsMenuCopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (_lastClickedPropPair.HasValue)
        {
            Clipboard.SetText(_lastClickedPropPair.Value.Key);
            TxtStatus.Text = $"🔑 Скопировано имя свойства: {_lastClickedPropPair.Value.Key}";
        }
    }

    private void ModalPropsMenuCopyRow_Click(object sender, RoutedEventArgs e)
    {
        CopyModalPropsSelection();
    }

    private void ModalPropsMenuCopyAllJson_Click(object sender, RoutedEventArgs e)
    {
        if (GridModalProps.ItemsSource is IEnumerable<KeyValuePair<string, string>> props)
        {
            var dict = props.ToDictionary(k => k.Key, v => v.Value);
            var json = JsonSerializer.Serialize(dict, PrettyJson);
            Clipboard.SetText(json);
            TxtStatus.Text = "📦 Все свойства события скопированы в формате JSON.";
        }
    }

    private void CopyModalPropsSelection()
    {
        var items = GridModalProps.SelectedItems.Cast<KeyValuePair<string, string>>().ToList();
        if (items.Count == 0 && _lastClickedPropPair.HasValue) items.Add(_lastClickedPropPair.Value);
        if (items.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var p in items)
        {
            sb.AppendLine($"{p.Key}\t{p.Value}");
        }
        Clipboard.SetText(sb.ToString().TrimEnd());
        TxtStatus.Text = $"📑 Скопировано {items.Count} строк свойств в буфер обмена.";
    }

    /// <summary>
    /// Справочник описаний для известных событий Технологического Журнала 1С.
    /// </summary>
    private static readonly Dictionary<string, string> KnownTgEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EXCP"] = "Ошибки платформы и конфигурации",
        ["EXCPCNTX"] = "Контекст места возникновения ошибки",
        ["QERR"] = "Ошибки компиляции/выполнения запросов",
        ["DBMSSQL"] = "Запросы к СУБД MS SQL Server",
        ["DBPOSTGRS"] = "Запросы к СУБД PostgreSQL",
        ["DBORACLE"] = "Запросы к СУБД Oracle Database",
        ["DBIBMDB2"] = "Запросы к СУБД IBM DB2",
        ["DBV8DBENG"] = "Запросы файловой СУБД 1С",
        ["SDBL"] = "Запросы внутреннего языка 1С (SDBL)",
        ["TLOCK"] = "Установка и ожидание блокировок 1С",
        ["TDEADLOCK"] = "Взаимоблокировки ресурсов (Deadlock)",
        ["TTIMEOUT"] = "Таймаут ожидания блокировки ресурса",
        ["CALL"] = "Серверные контекстные вызовы (CALL)",
        ["SCALL"] = "Удаленные вызовы процедур кластера",
        ["RUNMETH"] = "Исполнение процедур/функций модулей",
        ["LONGDURATIONINFO"] = "Длительная выполняющаяся операция",
        ["ADDIN"] = "Вызовы внешних компонент (Native/COM)",
        ["ADMIN"] = "Команды администрирования кластера",
        ["ATTN"] = "Предупреждения и системные уведомления",
        ["Context"] = "Стек контекста выполнения кода",
        ["CONN"] = "Установка и разрыв соединений",
        ["SESN"] = "Создание и завершение сеансов 1С",
        ["MEM"] = "Потребление оперативной памяти",
        ["PROC"] = "События рабочих процессов (rphost)",
        ["VRSREQUEST"] = "Входящие HTTP/Web-сервисы запросы",
        ["VRSRESPONSE"] = "Ответы HTTP/Web-сервисов",
        ["HASP"] = "Проверка аппаратных ключей защиты HASP",
        ["LIC"] = "Проверка программных лицензий 1С",
        ["PERF"] = "Замеры счетчиков производительности",
        ["LEAKS"] = "Утечки дескрипторов и памяти",
        ["SCOM"] = "Сетевой обмен между процессами кластера",
        ["SDISPATCH"] = "Диспетчеризация клиентских запросов",
        ["CLSTR"] = "Состояние и топология кластера 1С",
        ["RESTP"] = "Перезапуск и ротация рабочих процессов"
    };

    private void PopulateTgEventFilterItems()
    {
        if (CmbTgEventFilter == null) return;

        CmbTgEventFilter.SelectionChanged -= CmbTgEventFilter_SelectionChanged;

        var selectedText = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? (CmbTgEventFilter.SelectedItem as string);
        var selectedTag = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Tag as string;

        CmbTgEventFilter.Items.Clear();
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Все события 1С" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Ошибки (EXCP / EXCPCNTX / QERR)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Блокировки (TLOCK / TDEADLOCK / TTIMEOUT)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Запросы СУБД (DBMSSQL / SDBL / DBPOSTGRS)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Вызовы (CALL / SCALL / RUNMETH)" });
        CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = "Длительные (LONGDURATIONINFO)" });

        if (_techLogDocs.Count == 0)
        {
            // Логи еще не загружены — показываем полный справочник всех известных событий 1С
            foreach (var (ev, desc) in KnownTgEvents.OrderBy(k => k.Key))
            {
                CmbTgEventFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc}",
                    Tag = ev
                });
            }
        }
        else
        {
            // Логи загружены: подсчитываем количество событий
            var eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in _techLogDocs)
            {
                if (string.IsNullOrWhiteSpace(doc.Event)) continue;
                eventCounts[doc.Event] = eventCounts.GetValueOrDefault(doc.Event, 0) + 1;
            }

            // 1. Сначала события, найденные в текущем логе (сверху, отсортированные по алфавиту)
            var presentEvents = eventCounts.Keys.OrderBy(e => e).ToList();
            foreach (var ev in presentEvents)
            {
                var count = eventCounts[ev];
                string itemContent = KnownTgEvents.TryGetValue(ev, out var desc)
                    ? $"{ev} — {desc} ({count:N0})"
                    : $"{ev} ({count:N0})";

                CmbTgEventFilter.Items.Add(new ComboBoxItem { Content = itemContent, Tag = ev });
            }

            // 2. Затем остальные события из справочника (со счетчиком 0)
            var absentKnown = KnownTgEvents.Keys
                .Where(k => !eventCounts.ContainsKey(k))
                .OrderBy(k => k);

            foreach (var ev in absentKnown)
            {
                var desc = KnownTgEvents[ev];
                CmbTgEventFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc} (0)",
                    Tag = ev
                });
            }
        }

        int initialIndex = CmbTgEventFilter.SelectedIndex;
        int targetIndex = 0;
        if (!string.IsNullOrEmpty(selectedTag))
        {
            for (int i = 0; i < CmbTgEventFilter.Items.Count; i++)
            {
                if (CmbTgEventFilter.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }
        
        if (targetIndex == 0 && !string.IsNullOrEmpty(selectedText))
        {
            for (int i = 0; i < CmbTgEventFilter.Items.Count; i++)
            {
                if (CmbTgEventFilter.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), selectedText, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex == 0 && initialIndex > 0 && initialIndex < 6)
        {
            targetIndex = initialIndex;
        }

        CmbTgEventFilter.SelectedIndex = targetIndex;
        CmbTgEventFilter.SelectionChanged += CmbTgEventFilter_SelectionChanged;
    }

    private void PopulateTgExcludeFilterItems()
    {
        if (ListTgExcludeEvents == null) return;

        var previouslyChecked = _tgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _tgExcludeEventItems.Clear();

        foreach (var (ev, desc) in KnownTgEvents.OrderBy(k => k.Key))
        {
            _tgExcludeEventItems.Add(new FilterCheckItem
            {
                Tag = ev,
                Title = ev,
                Subtitle = desc,
                IsChecked = previouslyChecked.Contains(ev)
            });
        }

        FilterTgExcludeEventList();
    }

    private void TxtTgExcludeEventSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterTgExcludeEventList();
    }

    private void FilterTgExcludeEventList()
    {
        if (ListTgExcludeEvents == null) return;
        var search = TxtTgExcludeEventSearch?.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(search))
        {
            ListTgExcludeEvents.ItemsSource = _tgExcludeEventItems;
        }
        else
        {
            ListTgExcludeEvents.ItemsSource = _tgExcludeEventItems.Where(x =>
                x.Tag.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private void BtnTgExcludeClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _tgExcludeEventItems)
        {
            item.IsChecked = false;
        }
        RefreshTgFilter();
    }

    private void ChkTgExcludeEventItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshTgFilter();
    }

    private void ChkTgFilter_Click(object sender, RoutedEventArgs e)
    {
        RefreshTgFilter();
    }

    #region Отбор по полям ТЖ (Пользователи, Приложения, PID, SPID, OSThread)

    public void PopulateTgFieldValues()
    {
        if (_techLogDocs.Count == 0) return;

        var userCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var appCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pidCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var spidCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var threadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in _techLogDocs)
        {
            if (!string.IsNullOrEmpty(doc.User)) userCounts[doc.User] = userCounts.GetValueOrDefault(doc.User) + 1;
            if (!string.IsNullOrEmpty(doc.App)) appCounts[doc.App] = appCounts.GetValueOrDefault(doc.App) + 1;
            if (!string.IsNullOrEmpty(doc.ProcessId)) pidCounts[doc.ProcessId] = pidCounts.GetValueOrDefault(doc.ProcessId) + 1;
            if (!string.IsNullOrEmpty(doc.Spid)) spidCounts[doc.Spid] = spidCounts.GetValueOrDefault(doc.Spid) + 1;
            if (!string.IsNullOrEmpty(doc.OSThread)) threadCounts[doc.OSThread] = threadCounts.GetValueOrDefault(doc.OSThread) + 1;
        }

        ApplyFieldCounts(userCounts, _tgUserItems, "Пользователь", ListTgUsers);
        ApplyFieldCounts(appCounts, _tgAppItems, "Приложение", ListTgApps);
        ApplyFieldCounts(pidCounts, _tgPidItems, "PID", ListTgPids);
        ApplyFieldCounts(spidCounts, _tgSpidItems, "SPID", ListTgSpids);
        ApplyFieldCounts(threadCounts, _tgThreadItems, "Поток OS", ListTgThreads);
    }

    private static void ApplyFieldCounts(Dictionary<string, int> counts, ObservableCollection<FieldValueItem> collection, string category, ItemsControl? control)
    {
        var incSet = collection.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exSet = collection.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        collection.Clear();

        foreach (var (val, count) in counts.OrderByDescending(g => g.Value).ThenBy(g => g.Key))
        {
            collection.Add(new FieldValueItem
            {
                Category = category,
                Value = val,
                Count = count,
                IsInclude = incSet.Contains(val),
                IsExclude = exSet.Contains(val)
            });
        }

        if (control != null) control.ItemsSource = collection;
    }

    private void ChkTgFieldValueItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshTgFilter();
    }

    private void BtnTgClearAllFieldFilters_Click(object sender, RoutedEventArgs e)
    {
        ClearFieldCollection(_tgUserItems, true);
        ClearFieldCollection(_tgAppItems, true);
        ClearFieldCollection(_tgPidItems, true);
        ClearFieldCollection(_tgSpidItems, true);
        ClearFieldCollection(_tgThreadItems, true);
    }

    private void TxtTgSearchUsers_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchUsers.Text, _tgUserItems, ListTgUsers);
    private void TxtTgSearchApps_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchApps.Text, _tgAppItems, ListTgApps);
    private void TxtTgSearchPids_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchPids.Text, _tgPidItems, ListTgPids);
    private void TxtTgSearchSpids_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchSpids.Text, _tgSpidItems, ListTgSpids);
    private void TxtTgSearchThreads_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtTgSearchThreads.Text, _tgThreadItems, ListTgThreads);

    private static void FilterFieldControl(string query, ObservableCollection<FieldValueItem> source, ItemsControl? control)
    {
        if (control == null) return;
        query = query?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            control.ItemsSource = source;
        }
        else
        {
            control.ItemsSource = source.Where(x => x.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private void BtnTgSelectAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgUserItems, true);
    private void BtnTgExcludeAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgUserItems, true);
    private void BtnTgClearUsers_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgUserItems, true);

    private void BtnTgSelectAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgAppItems, true);
    private void BtnTgExcludeAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgAppItems, true);
    private void BtnTgClearApps_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgAppItems, true);

    private void BtnTgSelectAllPids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgPidItems, true);
    private void BtnTgExcludeAllPids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgPidItems, true);
    private void BtnTgClearPids_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgPidItems, true);

    private void BtnTgSelectAllSpids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgSpidItems, true);
    private void BtnTgExcludeAllSpids_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgSpidItems, true);
    private void BtnTgClearSpids_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgSpidItems, true);

    private void BtnTgSelectAllThreads_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_tgThreadItems, true);
    private void BtnTgExcludeAllThreads_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_tgThreadItems, true);
    private void BtnTgClearThreads_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_tgThreadItems, true);

    private void SetFieldCollectionInclude(ObservableCollection<FieldValueItem> collection, bool isTg)
    {
        foreach (var item in collection)
        {
            item.IsInclude = true;
            item.IsExclude = false;
        }
        RefreshFieldFilters(isTg);
    }

    private void SetFieldCollectionExclude(ObservableCollection<FieldValueItem> collection, bool isTg)
    {
        foreach (var item in collection)
        {
            item.IsExclude = true;
            item.IsInclude = false;
        }
        RefreshFieldFilters(isTg);
    }

    private void ClearFieldCollection(ObservableCollection<FieldValueItem> collection, bool isTg)
    {
        foreach (var item in collection)
        {
            item.IsInclude = false;
            item.IsExclude = false;
        }
        RefreshFieldFilters(isTg);
    }

    private void RefreshFieldFilters(bool isTg)
    {
        if (isTg) RefreshTgFilter();
        else RefreshLgFilter();
    }

    private void RefreshTgFilter()
    {
        _currentTgSnapshot = BuildTgFilterSnapshot();
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void RefreshLgFilter()
    {
        _currentLgSnapshot = BuildLgFilterSnapshot();
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    #endregion

    private static readonly char[] SearchSeparators = [' ', ',', ';', '\r', '\n', '\t'];

    private static (string Token, bool IsNegative)[] ParseSearchTokens(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var rawTokens = query.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<(string Token, bool IsNegative)>(rawTokens.Length);
        foreach (var rawToken in rawTokens)
        {
            var token = rawToken.Trim().Trim(',', ';', '"', '\'');
            if (token.Length == 0) continue;

            bool isNegative = false;
            if (token.StartsWith('!') || token.StartsWith('-'))
            {
                isNegative = true;
                token = token[1..].Trim().Trim(',', ';', '"', '\'');
                if (token.Length == 0) continue;
            }
            else if (token.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase) || token.StartsWith("НЕ:", StringComparison.OrdinalIgnoreCase))
            {
                isNegative = true;
                token = token[4..].Trim().Trim(',', ';', '"', '\'');
                if (token.Length == 0) continue;
            }

            list.Add((token, isNegative));
        }
        return list.ToArray();
    }

    private static bool MatchSearchQuery(TechLogDoc doc, string query)
    {
        var tokens = ParseSearchTokens(query);
        foreach (var (token, isNegative) in tokens)
        {
            bool found = ContainsTgToken(doc, token);
            if (isNegative ? found : !found) return false;
        }
        return true;
    }

    internal static bool ContainsTgToken(TechLogDoc doc, string token)
    {
        var colonIdx = token.IndexOf(':');
        if (colonIdx > 0 && colonIdx < token.Length - 1)
        {
            var field = token[..colonIdx].ToLowerInvariant();
            var val = token[(colonIdx + 1)..];

            return field switch
            {
                "user" or "usr" or "пользователь" => doc.User != null && doc.User.Contains(val, StringComparison.OrdinalIgnoreCase),
                "event" or "событие" => doc.Event != null && doc.Event.Contains(val, StringComparison.OrdinalIgnoreCase),
                "process" or "процесс" => doc.ProcessName != null && doc.ProcessName.Contains(val, StringComparison.OrdinalIgnoreCase),
                "pid" => doc.ProcessId != null && doc.ProcessId.Contains(val, StringComparison.OrdinalIgnoreCase),
                "spid" => doc.Spid != null && doc.Spid.Contains(val, StringComparison.OrdinalIgnoreCase),
                "thread" or "osthread" or "поток" => doc.OSThread != null && doc.OSThread.Contains(val, StringComparison.OrdinalIgnoreCase),
                "app" or "приложение" => doc.App != null && doc.App.Contains(val, StringComparison.OrdinalIgnoreCase),
                "context" or "контекст" => doc.Context != null && doc.Context.Contains(val, StringComparison.OrdinalIgnoreCase),
                "sql" => doc.Sql != null && doc.Sql.Contains(val, StringComparison.OrdinalIgnoreCase),
                "status" or "статус" => doc.ExecutionStatus.Contains(val, StringComparison.OrdinalIgnoreCase),
                "session" or "сеанс" => doc.SessionId != null && doc.SessionId.Contains(val, StringComparison.OrdinalIgnoreCase),
                _ => ContainsAnyTgField(doc, token)
            };
        }

        return ContainsAnyTgField(doc, token);
    }

    internal static bool ContainsAnyTgField(TechLogDoc doc, string token)
    {
        return (!string.IsNullOrEmpty(doc.Event) && doc.Event.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Spid) && doc.Spid.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.OSThread) && doc.OSThread.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.LongInfoName) && doc.LongInfoName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ProcessId) && doc.ProcessId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.SessionId) && doc.SessionId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ConnectId) && doc.ConnectId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ClientId) && doc.ClientId.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.ProcessName) && doc.ProcessName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.User) && doc.User.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.App) && doc.App.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Context) && doc.Context.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Sql) && doc.Sql.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Descr) && doc.Descr.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               doc.ExecutionStatus.Contains(token, StringComparison.OrdinalIgnoreCase) ||
               (doc.Properties.Count > 0 && doc.Properties.Values.Any(v => v.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private TgFilterSnapshot BuildTgFilterSnapshot()
    {
        var search = TxtTgFilter?.Text?.Trim();
        var searchTokens = ParseSearchTokens(search);

        var eventIndex = CmbTgEventFilter?.SelectedIndex ?? 0;
        var selectedItem = CmbTgEventFilter?.SelectedItem as ComboBoxItem;
        var eventTag = selectedItem?.Tag as string;
        var eventText = selectedItem?.Content?.ToString();

        var excludedEvents = _tgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var incRunning = ChkTgIncRunning?.IsChecked == true;
        var incCompleted = ChkTgIncCompleted?.IsChecked == true;
        var exRunning = ChkTgExRunning?.IsChecked == true;
        var exCompleted = ChkTgExCompleted?.IsChecked == true;

        var exRphost = ChkTgExRphost?.IsChecked == true;
        var exRmngr = ChkTgExRmngr?.IsChecked == true;
        var exRagent = ChkTgExRagent?.IsChecked == true;

        var minDurationMs = CmbTgMinDuration?.SelectedIndex switch
        {
            1 => 10,
            2 => 100,
            3 => 1000,
            4 => 5000,
            5 => 10000,
            _ => 0
        };

        var dateFrom = _tgFilterDateFrom?.Date;
        var dateTo = _tgFilterDateTo?.Date;

        var usersInc = _tgUserItems.Any(x => x.IsInclude) ? _tgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var usersEx = _tgUserItems.Any(x => x.IsExclude) ? _tgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var appsInc = _tgAppItems.Any(x => x.IsInclude) ? _tgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var appsEx = _tgAppItems.Any(x => x.IsExclude) ? _tgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var pidsInc = _tgPidItems.Any(x => x.IsInclude) ? _tgPidItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var pidsEx = _tgPidItems.Any(x => x.IsExclude) ? _tgPidItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var spidsInc = _tgSpidItems.Any(x => x.IsInclude) ? _tgSpidItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var spidsEx = _tgSpidItems.Any(x => x.IsExclude) ? _tgSpidItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var threadsInc = _tgThreadItems.Any(x => x.IsInclude) ? _tgThreadItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var threadsEx = _tgThreadItems.Any(x => x.IsExclude) ? _tgThreadItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        return new TgFilterSnapshot(
            search,
            searchTokens,
            eventIndex,
            eventTag,
            eventText,
            excludedEvents,
            incRunning, incCompleted,
            exRunning, exCompleted,
            exRphost, exRmngr, exRagent,
            minDurationMs,
            dateFrom, dateTo,
            _tgHasTimeFrom, _tgTimeFrom, _tgExactFrom,
            _tgHasTimeTo, _tgTimeTo, _tgExactTo,
            usersInc, usersEx,
            appsInc, appsEx,
            pidsInc, pidsEx,
            spidsInc, spidsEx,
            threadsInc, threadsEx);
    }

    private bool FilterTgDoc(object item)
    {
        if (item is not TechLogDoc doc) return false;
        return _currentTgSnapshot.Matches(doc);
    }

    private void TxtTgFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshTgFilter();
    }

    private void TxtTgTimeFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _tgHasTimeFrom = TryParseTimeFilter(TxtTgTimeFrom?.Text, out _tgTimeFrom, out _tgExactFrom);
        _tgHasTimeTo = TryParseTimeFilter(TxtTgTimeTo?.Text, out _tgTimeTo, out _tgExactTo);
        RefreshTgFilter();
    }

    private void CmbTgEventFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshTgFilter();
    }

    private void CmbTgMinDuration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshTgFilter();
    }

    private void CmbTgSortPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyTgSortPreset();
    }

    private void ApplyTgSortPreset()
    {
        _activeTgSorts.Clear();

        if (CmbTgSortPreset?.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString() ?? string.Empty;
            if (text.Contains("сначала новые"))
            {
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("сначала старые"))
            {
                _activeTgSorts.Add(("Date", ListSortDirection.Ascending));
            }
            else if (text.Contains("сначала долгие"))
            {
                _activeTgSorts.Add(("DurationMs", ListSortDirection.Descending));
            }
            else if (text.Contains("Длительность + Новые дата"))
            {
                _activeTgSorts.Add(("DurationMs", ListSortDirection.Descending));
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("Событие + Длительность"))
            {
                _activeTgSorts.Add(("Event", ListSortDirection.Ascending));
                _activeTgSorts.Add(("DurationMs", ListSortDirection.Descending));
            }
            else if (text.Contains("Пользователь + Дата"))
            {
                _activeTgSorts.Add(("User", ListSortDirection.Ascending));
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else
            {
                _activeTgSorts.Add(("Date", ListSortDirection.Descending));
            }
        }
        else
        {
            _activeTgSorts.Add(("Date", ListSortDirection.Descending));
        }

        if (_techLogDocs != null && _techLogDocs.Count > 0)
        {
            SortTechLogDocs(_techLogDocs, _activeTgSorts);
        }

        if (GridTg?.Columns != null)
        {
            foreach (var col in GridTg.Columns)
            {
                var match = _activeTgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }
        }

        _tgCollectionView?.Refresh();
    }

    private void BtnResetTgFilters_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "Вы действительно хотите полностью очистить таблицу Технологического Журнала?",
            "Очистка таблицы ТЖ",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (TxtTgFilter != null) TxtTgFilter.Text = string.Empty;
        if (TxtTgTimeFrom != null) TxtTgTimeFrom.Text = string.Empty;
        if (TxtTgTimeTo != null) TxtTgTimeTo.Text = string.Empty;
        _tgHasTimeFrom = false;
        _tgHasTimeTo = false;
        SetTgDateRange(null, null);
        if (CmbTgEventFilter != null) CmbTgEventFilter.SelectedIndex = 0;
        foreach (var item in _tgExcludeEventItems) item.IsChecked = false;
        if (ChkTgIncRunning != null) ChkTgIncRunning.IsChecked = false;
        if (ChkTgIncCompleted != null) ChkTgIncCompleted.IsChecked = false;
        if (ChkTgExRphost != null) ChkTgExRphost.IsChecked = false;
        if (ChkTgExRmngr != null) ChkTgExRmngr.IsChecked = false;
        if (ChkTgExRagent != null) ChkTgExRagent.IsChecked = false;
        if (ChkTgExCompleted != null) ChkTgExCompleted.IsChecked = false;
        if (ChkTgExRunning != null) ChkTgExRunning.IsChecked = false;
        if (CmbTgMinDuration != null) CmbTgMinDuration.SelectedIndex = 0;
        if (CmbTgSortPreset != null) CmbTgSortPreset.SelectedIndex = 0;

        ClearFieldCollection(_tgUserItems, false);
        ClearFieldCollection(_tgAppItems, false);
        ClearFieldCollection(_tgPidItems, false);
        ClearFieldCollection(_tgSpidItems, false);
        ClearFieldCollection(_tgThreadItems, false);

        if (BtnTgMoreChips != null) BtnTgMoreChips.IsChecked = false;

        _techLogDocs.Clear();
        _activeTgSorts.Clear();
        if (GridTg?.Columns != null)
        {
            foreach (var col in GridTg.Columns) col.SortDirection = null;
        }

        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        TxtStatus.Text = "Таблица Технологического Журнала очищена.";
    }

    private void UpdateTgCountStatus()
    {
        if (TxtTgCountBadge == null) return;
        var total = _techLogDocs.Count;
        var count = _tgCollectionView is System.Collections.ICollection col ? col.Count : total;
        TxtTgCountBadge.Text = $"{count:N0} записей";
    }

    #region Фильтрация и Поиск ЖР (EventLog)

    /// <summary>
    /// Справочник описаний для известных системных событий Журнала Регистрации 1С.
    /// </summary>
    private static readonly Dictionary<string, string> KnownLgEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_$Data$_.Post"] = "Проведение документа",
        ["_$Data$_.Unpost"] = "Отмена проведения",
        ["_$Data$_.New"] = "Создание нового объекта",
        ["_$Data$_.Update"] = "Изменение данных объекта",
        ["_$Data$_.Delete"] = "Удаление объекта базы",
        ["_$Data$_.TotalsRecalculation"] = "Пересчет итогов регистров",
        ["_$Session$_.Start"] = "Начало сеанса пользователя",
        ["_$Session$_.Finish"] = "Завершение сеанса",
        ["_$Session$_.Authentication"] = "Успешная аутентификация",
        ["_$Session$_.AuthenticationError"] = "Ошибка аутентификации",
        ["_$User$_.New"] = "Создание пользователя",
        ["_$User$_.Update"] = "Изменение пользователя",
        ["_$User$_.Delete"] = "Удаление пользователя",
        ["_$Job$_.Start"] = "Старт фонового задания",
        ["_$Job$_.Finish"] = "Успех фонового задания",
        ["_$Job$_.Fail"] = "Ошибка фонового задания",
        ["_$Access$_.AccessDenied"] = "Отказ в доступе по RLS",
        ["_$Transaction$_.Commit"] = "Фиксация транзакции",
        ["_$Transaction$_.Rollback"] = "Откат транзакции",
        ["_$InfoBase$_.ConfigUpdate"] = "Обновление конфигурации БД"
    };

    private void PopulateLgEventFilterItems()
    {
        if (CmbLgImportanceFilter == null) return;

        CmbLgImportanceFilter.SelectionChanged -= CmbLgImportanceFilter_SelectionChanged;

        var selectedText = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? (CmbLgImportanceFilter.SelectedItem as string);
        var selectedTag = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Tag as string;

        CmbLgImportanceFilter.Items.Clear();
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Все уровни и события" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Ошибка" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Предупреждение" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Информация" });
        CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = "Примечание" });

        if (_eventLogDocs.Count == 0)
        {
            // Логи еще не загружены — показываем полный справочник системных событий ЖР
            foreach (var (ev, desc) in KnownLgEvents.OrderBy(k => k.Key))
            {
                CmbLgImportanceFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc}",
                    Tag = ev
                });
            }
        }
        else
        {
            var eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in _eventLogDocs)
            {
                if (string.IsNullOrWhiteSpace(doc.Event)) continue;
                eventCounts[doc.Event] = eventCounts.GetValueOrDefault(doc.Event, 0) + 1;
            }

            // 1. Сначала события, найденные в текущем ЖР
            var presentEvents = eventCounts.Keys.OrderBy(e => e).ToList();
            foreach (var ev in presentEvents)
            {
                var count = eventCounts[ev];
                string itemContent = KnownLgEvents.TryGetValue(ev, out var desc)
                    ? $"{ev} — {desc} ({count:N0})"
                    : $"{ev} ({count:N0})";

                CmbLgImportanceFilter.Items.Add(new ComboBoxItem { Content = itemContent, Tag = ev });
            }

            // 2. Затем остальные события из справочника (со счетчиком 0)
            var absentKnown = KnownLgEvents.Keys
                .Where(k => !eventCounts.ContainsKey(k))
                .OrderBy(k => k);

            foreach (var ev in absentKnown)
            {
                var desc = KnownLgEvents[ev];
                CmbLgImportanceFilter.Items.Add(new ComboBoxItem
                {
                    Content = $"{ev} — {desc} (0)",
                    Tag = ev
                });
            }
        }

        int initialIndex = CmbLgImportanceFilter.SelectedIndex;
        int targetIndex = 0;
        if (!string.IsNullOrEmpty(selectedTag))
        {
            for (int i = 0; i < CmbLgImportanceFilter.Items.Count; i++)
            {
                if (CmbLgImportanceFilter.Items[i] is ComboBoxItem item && string.Equals(item.Tag as string, selectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex == 0 && !string.IsNullOrEmpty(selectedText))
        {
            for (int i = 0; i < CmbLgImportanceFilter.Items.Count; i++)
            {
                if (CmbLgImportanceFilter.Items[i] is ComboBoxItem item && string.Equals(item.Content?.ToString(), selectedText, StringComparison.OrdinalIgnoreCase))
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex == 0 && initialIndex > 0 && initialIndex < 5)
        {
            targetIndex = initialIndex;
        }

        CmbLgImportanceFilter.SelectedIndex = targetIndex;
        CmbLgImportanceFilter.SelectionChanged += CmbLgImportanceFilter_SelectionChanged;
    }

    private void PopulateLgExcludeFilterItems()
    {
        if (ListLgExcludeEvents == null) return;

        var previouslyChecked = _lgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _lgExcludeEventItems.Clear();

        foreach (var (ev, desc) in KnownLgEvents.OrderBy(k => k.Key))
        {
            _lgExcludeEventItems.Add(new FilterCheckItem
            {
                Tag = ev,
                Title = ev,
                Subtitle = desc,
                IsChecked = previouslyChecked.Contains(ev)
            });
        }

        FilterLgExcludeEventList();
    }

    private void TxtLgExcludeEventSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterLgExcludeEventList();
    }

    private void FilterLgExcludeEventList()
    {
        if (ListLgExcludeEvents == null) return;
        var search = TxtLgExcludeEventSearch?.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(search))
        {
            ListLgExcludeEvents.ItemsSource = _lgExcludeEventItems;
        }
        else
        {
            ListLgExcludeEvents.ItemsSource = _lgExcludeEventItems.Where(x =>
                x.Tag.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    private void BtnLgExcludeClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _lgExcludeEventItems)
        {
            item.IsChecked = false;
        }
        RefreshLgFilter();
    }

    private void ChkLgExcludeEventItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshLgFilter();
    }

    private void ChkLgFilter_Click(object sender, RoutedEventArgs e)
    {
        RefreshLgFilter();
    }

    #region Отбор по полям ЖР (Пользователи, Приложения, Метаданные, События)

    public async Task PopulateLgFieldValuesAsync()
    {
        if (_eventLogDocs.Count == 0) return;

        Dictionary<string, int> userCounts;
        Dictionary<string, int> appCounts;
        Dictionary<string, int> metaCounts;
        Dictionary<string, int> eventCounts;

        if (_eventLogDocs.Count > 100_000)
        {
            (userCounts, appCounts, metaCounts, eventCounts) = await Task.Run(() =>
            {
                var uc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var ac = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var mc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var ec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var doc in _eventLogDocs)
                {
                    if (!string.IsNullOrEmpty(doc.User)) uc[doc.User] = uc.GetValueOrDefault(doc.User) + 1;
                    if (!string.IsNullOrEmpty(doc.App)) ac[doc.App] = ac.GetValueOrDefault(doc.App) + 1;
                    if (!string.IsNullOrEmpty(doc.Meta)) mc[doc.Meta] = mc.GetValueOrDefault(doc.Meta) + 1;
                    if (!string.IsNullOrEmpty(doc.Event)) ec[doc.Event] = ec.GetValueOrDefault(doc.Event) + 1;
                }

                return (uc, ac, mc, ec);
            }).ConfigureAwait(true);
        }
        else
        {
            userCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            appCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            metaCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var doc in _eventLogDocs)
            {
                if (!string.IsNullOrEmpty(doc.User)) userCounts[doc.User] = userCounts.GetValueOrDefault(doc.User) + 1;
                if (!string.IsNullOrEmpty(doc.App)) appCounts[doc.App] = appCounts.GetValueOrDefault(doc.App) + 1;
                if (!string.IsNullOrEmpty(doc.Meta)) metaCounts[doc.Meta] = metaCounts.GetValueOrDefault(doc.Meta) + 1;
                if (!string.IsNullOrEmpty(doc.Event)) eventCounts[doc.Event] = eventCounts.GetValueOrDefault(doc.Event) + 1;
            }
        }

        ApplyFieldCounts(userCounts, _lgUserItems, "Пользователь", ListLgUsers);
        ApplyFieldCounts(appCounts, _lgAppItems, "Приложение", ListLgApps);
        ApplyFieldCounts(metaCounts, _lgMetaItems, "Метаданные", ListLgMetas);
        ApplyFieldCounts(eventCounts, _lgEventFieldItems, "Событие", ListLgEvents);
    }

    public void PopulateLgFieldValues()
    {
        if (_eventLogDocs.Count == 0) return;

        var userCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var appCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var metaCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var eventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var doc in _eventLogDocs)
        {
            if (!string.IsNullOrEmpty(doc.User)) userCounts[doc.User] = userCounts.GetValueOrDefault(doc.User) + 1;
            if (!string.IsNullOrEmpty(doc.App)) appCounts[doc.App] = appCounts.GetValueOrDefault(doc.App) + 1;
            if (!string.IsNullOrEmpty(doc.Meta)) metaCounts[doc.Meta] = metaCounts.GetValueOrDefault(doc.Meta) + 1;
            if (!string.IsNullOrEmpty(doc.Event)) eventCounts[doc.Event] = eventCounts.GetValueOrDefault(doc.Event) + 1;
        }

        ApplyFieldCounts(userCounts, _lgUserItems, "Пользователь", ListLgUsers);
        ApplyFieldCounts(appCounts, _lgAppItems, "Приложение", ListLgApps);
        ApplyFieldCounts(metaCounts, _lgMetaItems, "Метаданные", ListLgMetas);
        ApplyFieldCounts(eventCounts, _lgEventFieldItems, "Событие", ListLgEvents);
    }

    private void ChkLgFieldValueItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshLgFilter();
    }

    private void BtnLgClearAllFieldFilters_Click(object sender, RoutedEventArgs e)
    {
        ClearFieldCollection(_lgUserItems, false);
        ClearFieldCollection(_lgAppItems, false);
        ClearFieldCollection(_lgMetaItems, false);
        ClearFieldCollection(_lgEventFieldItems, false);
    }

    private void TxtLgSearchUsers_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchUsers.Text, _lgUserItems, ListLgUsers);
    private void TxtLgSearchApps_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchApps.Text, _lgAppItems, ListLgApps);
    private void TxtLgSearchMetas_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchMetas.Text, _lgMetaItems, ListLgMetas);
    private void TxtLgSearchEvents_TextChanged(object sender, TextChangedEventArgs e) => FilterFieldControl(TxtLgSearchEvents.Text, _lgEventFieldItems, ListLgEvents);

    private void BtnLgSelectAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgUserItems, false);
    private void BtnLgExcludeAllUsers_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgUserItems, false);
    private void BtnLgClearUsers_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgUserItems, false);

    private void BtnLgSelectAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgAppItems, false);
    private void BtnLgExcludeAllApps_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgAppItems, false);
    private void BtnLgClearApps_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgAppItems, false);

    private void BtnLgSelectAllMetas_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgMetaItems, false);
    private void BtnLgExcludeAllMetas_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgMetaItems, false);
    private void BtnLgClearMetas_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgMetaItems, false);

    private void BtnLgSelectAllEvents_Click(object sender, RoutedEventArgs e) => SetFieldCollectionInclude(_lgEventFieldItems, false);
    private void BtnLgExcludeAllEvents_Click(object sender, RoutedEventArgs e) => SetFieldCollectionExclude(_lgEventFieldItems, false);
    private void BtnLgClearEvents_Click(object sender, RoutedEventArgs e) => ClearFieldCollection(_lgEventFieldItems, false);

    #endregion

    private static bool MatchLgSearchQuery(EventLogDoc doc, string query)
    {
        var tokens = ParseSearchTokens(query);
        foreach (var (token, isNegative) in tokens)
        {
            bool found = ContainsLgToken(doc, token);
            if (isNegative ? found : !found) return false;
        }

        return true;
    }

    internal static bool ContainsLgToken(EventLogDoc doc, string token)
    {
        var colonIdx = token.IndexOf(':');
        if (colonIdx > 0 && colonIdx < token.Length - 1)
        {
            var field = token[..colonIdx].ToLowerInvariant();
            var val = token[(colonIdx + 1)..];

            return field switch
            {
                "user" or "usr" or "пользователь" => doc.User != null && doc.User.Contains(val, StringComparison.OrdinalIgnoreCase),
                "comp" or "computer" or "компьютер" or "пк" => doc.Computer != null && doc.Computer.Contains(val, StringComparison.OrdinalIgnoreCase),
                "server" or "сервер" => doc.Server != null && doc.Server.Contains(val, StringComparison.OrdinalIgnoreCase),
                "event" or "событие" => doc.Event != null && doc.Event.Contains(val, StringComparison.OrdinalIgnoreCase),
                "level" or "важность" or "importance" => doc.Importance != null && doc.Importance.Contains(val, StringComparison.OrdinalIgnoreCase),
                "app" or "приложение" => (doc.App != null && doc.App.Contains(val, StringComparison.OrdinalIgnoreCase)) || (doc.AppTypeName != null && doc.AppTypeName.Contains(val, StringComparison.OrdinalIgnoreCase)),
                "meta" or "мета" or "метаданные" => doc.Meta != null && doc.Meta.Contains(val, StringComparison.OrdinalIgnoreCase),
                "data" or "данные" => (doc.DataPresentation != null && doc.DataPresentation.Contains(val, StringComparison.OrdinalIgnoreCase)) || (doc.Data != null && doc.Data.Contains(val, StringComparison.OrdinalIgnoreCase)),
                "session" or "сеанс" => doc.Session != null && doc.Session.Contains(val, StringComparison.OrdinalIgnoreCase),
                "file" or "файл" => doc.FileName != null && doc.FileName.Contains(val, StringComparison.OrdinalIgnoreCase),
                "comment" or "комментарий" => doc.Comment != null && doc.Comment.Contains(val, StringComparison.OrdinalIgnoreCase),
                _ => ContainsAnyLgField(doc, val)
            };
        }

        return ContainsAnyLgField(doc, token);
    }

    internal static bool ContainsAnyLgField(EventLogDoc doc, string token)
    {
        return (!string.IsNullOrEmpty(doc.Event) && doc.Event.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Importance) && doc.Importance.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.User) && doc.User.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Computer) && doc.Computer.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.App) && doc.App.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Meta) && doc.Meta.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.DataPresentation) && doc.DataPresentation.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Server) && doc.Server.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Session) && doc.Session.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Connection) && doc.Connection.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.FileName) && doc.FileName.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(doc.Comment) && doc.Comment.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private LgFilterSnapshot BuildLgFilterSnapshot()
    {
        var search = TxtLgFilter?.Text?.Trim();
        var searchTokens = ParseSearchTokens(search);

        var importanceIndex = CmbLgImportanceFilter?.SelectedIndex ?? 0;
        var selectedItem = CmbLgImportanceFilter?.SelectedItem as ComboBoxItem;
        var importanceTag = selectedItem?.Tag as string;
        var importanceText = selectedItem?.Content?.ToString();

        var incError = ChkLgIncError?.IsChecked == true;
        var incWarn = ChkLgIncWarn?.IsChecked == true;
        var incInfo = ChkLgIncInfo?.IsChecked == true;
        var incNote = ChkLgIncNote?.IsChecked == true;

        var exError = ChkLgExError?.IsChecked == true;
        var exWarn = ChkLgExWarn?.IsChecked == true;
        var exInfo = ChkLgExInfo?.IsChecked == true;
        var exNote = ChkLgExNote?.IsChecked == true;

        var excludedEvents = _lgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dateFrom = _lgFilterDateFrom?.Date;
        var dateTo = _lgFilterDateTo?.Date;

        var usersInc = _lgUserItems.Any(x => x.IsInclude) ? _lgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var usersEx = _lgUserItems.Any(x => x.IsExclude) ? _lgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var appsInc = _lgAppItems.Any(x => x.IsInclude) ? _lgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var appsEx = _lgAppItems.Any(x => x.IsExclude) ? _lgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var metasInc = _lgMetaItems.Any(x => x.IsInclude) ? _lgMetaItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var metasEx = _lgMetaItems.Any(x => x.IsExclude) ? _lgMetaItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var eventsInc = _lgEventFieldItems.Any(x => x.IsInclude) ? _lgEventFieldItems.Where(x => x.IsInclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var eventsEx = _lgEventFieldItems.Any(x => x.IsExclude) ? _lgEventFieldItems.Where(x => x.IsExclude).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        return new LgFilterSnapshot(
            search,
            searchTokens,
            importanceIndex,
            importanceTag,
            importanceText,
            incError, incWarn, incInfo, incNote,
            exError, exWarn, exInfo, exNote,
            excludedEvents,
            dateFrom, dateTo,
            _lgHasTimeFrom, _lgTimeFrom, _lgExactFrom,
            _lgHasTimeTo, _lgTimeTo, _lgExactTo,
            usersInc, usersEx,
            appsInc, appsEx,
            metasInc, metasEx,
            eventsInc, eventsEx);
    }

    private bool FilterLgDoc(object item)
    {
        if (item is not EventLogDoc doc) return false;
        return _currentLgSnapshot.Matches(doc);
    }

    private void TxtLgFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshLgFilter();
    }

    private void TxtLgTimeFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _lgHasTimeFrom = TryParseTimeFilter(TxtLgTimeFrom?.Text, out _lgTimeFrom, out _lgExactFrom);
        _lgHasTimeTo = TryParseTimeFilter(TxtLgTimeTo?.Text, out _lgTimeTo, out _lgExactTo);
        RefreshLgFilter();
    }

    private void CmbLgImportanceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshLgFilter();
    }

    private void CmbLgLimit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtStatus == null) return;
        var limit = GetPreviewLimit(CmbLgLimit);
        if (limit == int.MaxValue)
        {
            TxtStatus.Text = "ℹ️ Выбран режим «Все записи». Ограничений нет — будут загружены все записи. Для ускорения обработки больших логов можно задать время или фильтр важности.";
        }
    }

    private void CmbTgLimit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TxtStatus == null) return;
        var limit = GetPreviewLimit(CmbTgLimit);
        if (limit == int.MaxValue)
        {
            TxtStatus.Text = "ℹ️ Выбран режим «Все записи». Ограничений нет — будут загружены все записи. Для ускорения обработки больших логов можно задать время или фильтр событий.";
        }
    }

    private void CmbLgSortPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLgSortPreset();
    }

    private void ApplyLgSortPreset()
    {
        _activeLgSorts.Clear();

        if (CmbLgSortPreset?.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString() ?? string.Empty;
            if (text.Contains("сначала новые"))
            {
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("сначала старые"))
            {
                _activeLgSorts.Add(("Date", ListSortDirection.Ascending));
            }
            else if (text.Contains("Важность + Дата"))
            {
                _activeLgSorts.Add(("Importance", ListSortDirection.Ascending));
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("Событие + Дата"))
            {
                _activeLgSorts.Add(("Event", ListSortDirection.Ascending));
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else if (text.Contains("Пользователь + Дата"))
            {
                _activeLgSorts.Add(("User", ListSortDirection.Ascending));
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
            else
            {
                _activeLgSorts.Add(("Date", ListSortDirection.Descending));
            }
        }
        else
        {
            _activeLgSorts.Add(("Date", ListSortDirection.Descending));
        }

        if (_eventLogDocs != null && _eventLogDocs.Count > 0)
        {
            SortEventLogDocs(_eventLogDocs, _activeLgSorts);
        }

        if (GridLg?.Columns != null)
        {
            foreach (var col in GridLg.Columns)
            {
                var match = _activeLgSorts.FirstOrDefault(s => string.Equals(s.SortMemberPath, col.SortMemberPath, StringComparison.OrdinalIgnoreCase));
                col.SortDirection = match != default ? match.Direction : null;
            }
        }

        _lgCollectionView?.Refresh();
    }

    private void BtnResetLgFilters_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "Вы действительно хотите полностью очистить таблицу Журнала Регистрации?",
            "Очистка таблицы ЖР",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        if (TxtLgFilter != null) TxtLgFilter.Text = string.Empty;
        if (TxtLgTimeFrom != null) TxtLgTimeFrom.Text = string.Empty;
        if (TxtLgTimeTo != null) TxtLgTimeTo.Text = string.Empty;
        _lgHasTimeFrom = false;
        _lgHasTimeTo = false;
        SetLgDateRange(null, null);
        if (CmbLgImportanceFilter != null) CmbLgImportanceFilter.SelectedIndex = 0;
        foreach (var item in _lgExcludeEventItems) item.IsChecked = false;
        if (ChkLgIncError != null) ChkLgIncError.IsChecked = false;
        if (ChkLgIncWarn != null) ChkLgIncWarn.IsChecked = false;
        if (ChkLgIncInfo != null) ChkLgIncInfo.IsChecked = false;
        if (ChkLgIncNote != null) ChkLgIncNote.IsChecked = false;
        if (ChkLgExError != null) ChkLgExError.IsChecked = false;
        if (ChkLgExWarn != null) ChkLgExWarn.IsChecked = false;
        if (ChkLgExInfo != null) ChkLgExInfo.IsChecked = false;
        if (ChkLgExNote != null) ChkLgExNote.IsChecked = false;
        if (CmbLgSortPreset != null) CmbLgSortPreset.SelectedIndex = 0;

        ClearFieldCollection(_lgUserItems, false);
        ClearFieldCollection(_lgAppItems, false);
        ClearFieldCollection(_lgMetaItems, false);
        ClearFieldCollection(_lgEventFieldItems, false);

        if (BtnLgMoreChips != null) BtnLgMoreChips.IsChecked = false;

        _eventLogDocs.Clear();
        _activeLgSorts.Clear();
        if (GridLg?.Columns != null)
        {
            foreach (var col in GridLg.Columns) col.SortDirection = null;
        }

        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();

        GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, true, true);

        TxtStatus.Text = "Таблица Журнала Регистрации очищена.";
    }

    /// <summary>
    /// Универсальный разбор строки времени/даты для гибкой фильтрации.
    /// Поддерживает: "16:13:00", "16:13:50.875", "16:13", "2026-08-17 16:13:00", "17.08.2026 16:13".
    /// </summary>
    private static bool TryParseTimeFilter(string? input, out TimeSpan timeOfDay, out DateTime exactDate)
    {
        timeOfDay = default;
        exactDate = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        input = input.Trim();

        // 1. Полноформатная дата и время
        if (DateTime.TryParse(input, out exactDate))
        {
            timeOfDay = exactDate.TimeOfDay;
            return true;
        }

        // 2. Время суток (HH:mm:ss.fff / HH:mm:ss / HH:mm)
        if (TimeSpan.TryParse(input, out timeOfDay))
        {
            return true;
        }

        // 3. Формат без двоеточий или с точкой, например "16.13.00"
        var normalized = input.Replace('.', ':');
        if (TimeSpan.TryParse(normalized, out timeOfDay))
        {
            return true;
        }

        return false;
    }

    private void UpdateLgCountStatus()
    {
        if (TxtLgCountBadge == null) return;
        var total = _eventLogDocs.Count;
        var count = _lgCollectionView is System.Collections.ICollection col ? col.Count : total;
        TxtLgCountBadge.Text = $"{count:N0} записей";
    }

    #endregion

    #region Мульти-Сортировка (Multi-Column Sorting)

    private async void GridTg_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        await HandleMultiColumnSortingAsync(GridTg, _tgCollectionView, e.Column);
    }

    private async void GridLg_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        await HandleMultiColumnSortingAsync(GridLg, _lgCollectionView, e.Column);
    }

    private async Task HandleMultiColumnSortingAsync(DataGrid dataGrid, ICollectionView? collectionView, DataGridColumn column)
    {
        if (collectionView == null || string.IsNullOrEmpty(column.SortMemberPath)) return;

        var headerText = column.Header?.ToString() ?? column.SortMemberPath;
        var sortMemberPath = column.SortMemberPath;
        var newDirection = column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        var dirText = newDirection == ListSortDirection.Ascending ? "по возрастанию ▲" : "по убыванию ▼";
        TxtStatus.Text = $"⏳ Выполняется сортировка таблицы по колонке '{headerText}' ({dirText})...";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            // Отдаем квант времени для мгновенной отрисовки статуса и курсора ожидания
            await Task.Yield();

            bool isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            var isTg = ReferenceEquals(dataGrid, GridTg);
            var activeSorts = isTg ? _activeTgSorts : _activeLgSorts;

            if (!isShiftPressed)
            {
                foreach (var col in dataGrid.Columns)
                {
                    if (col != column) col.SortDirection = null;
                }
                activeSorts.Clear();
            }
            else
            {
                activeSorts.RemoveAll(s => string.Equals(s.SortMemberPath, sortMemberPath, StringComparison.OrdinalIgnoreCase));
            }

            column.SortDirection = newDirection;
            activeSorts.Insert(0, (sortMemberPath, newDirection));

            if (isTg)
            {
                SortTechLogDocs(_techLogDocs, activeSorts);
            }
            else
            {
                SortEventLogDocs(_eventLogDocs, activeSorts);
            }

            collectionView.SortDescriptions.Clear();
            collectionView.Refresh();

            TxtStatus.Text = $"Сортировка завершена: '{headerText}' ({dirText}).";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static void SortTechLogDocs(List<TechLogDoc> docs, IReadOnlyList<(string SortMemberPath, ListSortDirection Direction)> sorts)
    {
        if (docs.Count <= 1) return;
        if (sorts.Count == 0)
        {
            docs.Sort((a, b) => b.Date.CompareTo(a.Date));
            return;
        }

        docs.Sort((a, b) =>
        {
            foreach (var (prop, dir) in sorts)
            {
                int cmp = prop switch
                {
                    "Date" => a.Date.CompareTo(b.Date),
                    "DurationMs" or "Duration" => a.Duration.CompareTo(b.Duration),
                    "Event" => string.Compare(a.Event, b.Event, StringComparison.OrdinalIgnoreCase),
                    "IsActiveOperation" => a.IsActiveOperation.CompareTo(b.IsActiveOperation),
                    "ProcessName" => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase),
                    "ProcessId" => string.Compare(a.ProcessId, b.ProcessId, StringComparison.OrdinalIgnoreCase),
                    "Spid" => string.Compare(a.Spid, b.Spid, StringComparison.OrdinalIgnoreCase),
                    "OSThread" => string.Compare(a.OSThread, b.OSThread, StringComparison.OrdinalIgnoreCase),
                    "User" => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase),
                    "App" => string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase),
                    "Context" => string.Compare(a.Context, b.Context, StringComparison.OrdinalIgnoreCase),
                    _ => 0
                };

                if (cmp != 0)
                {
                    return dir == ListSortDirection.Ascending ? cmp : -cmp;
                }
            }
            return b.Date.CompareTo(a.Date);
        });
    }

    private static void SortEventLogDocs(List<EventLogDoc> docs, IReadOnlyList<(string SortMemberPath, ListSortDirection Direction)> sorts)
    {
        if (docs.Count <= 1) return;
        if (sorts.Count == 0)
        {
            docs.Sort((a, b) => b.Date.CompareTo(a.Date));
            return;
        }

        docs.Sort((a, b) =>
        {
            foreach (var (prop, dir) in sorts)
            {
                int cmp = prop switch
                {
                    "Date" => a.Date.CompareTo(b.Date),
                    "Event" => string.Compare(a.Event, b.Event, StringComparison.OrdinalIgnoreCase),
                    "User" => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase),
                    "App" => string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase),
                    "Meta" => string.Compare(a.Meta, b.Meta, StringComparison.OrdinalIgnoreCase),
                    "Tran" => string.Compare(a.Tran, b.Tran, StringComparison.OrdinalIgnoreCase),
                    "Comment" => string.Compare(a.Comment, b.Comment, StringComparison.OrdinalIgnoreCase),
                    "Importance" => string.Compare(a.Importance, b.Importance, StringComparison.OrdinalIgnoreCase),
                    "Data" => string.Compare(a.Data, b.Data, StringComparison.OrdinalIgnoreCase),
                    "Session" => string.Compare(a.Session, b.Session, StringComparison.OrdinalIgnoreCase),
                    "FileName" => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase),
                    "FileSize" => a.FileSize.CompareTo(b.FileSize),
                    _ => 0
                };

                if (cmp != 0)
                {
                    return dir == ListSortDirection.Ascending ? cmp : -cmp;
                }
            }
            return b.Date.CompareTo(a.Date);
        });
    }

    #endregion

    #region Копирование ячеек, строк и JSON через контекстное меню (ПКМ) и Ctrl+C

    private string _lastClickedTgCellText = "";
    private TechLogDoc? _lastClickedTgDoc;
    private string _lastClickedLgCellText = "";
    private EventLogDoc? _lastClickedLgDoc;

    private void GridTg_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cell = FindVisualParent<DataGridCell>(dep);
            if (cell != null)
            {
                if (cell.Content is TextBlock tb)
                {
                    _lastClickedTgCellText = tb.Text;
                }
                else if (cell.Content is ContentPresenter cp && cp.Content != null)
                {
                    _lastClickedTgCellText = cp.Content.ToString() ?? "";
                }
            }

            var row = FindVisualParent<DataGridRow>(dep);
            if (row != null && row.Item is TechLogDoc doc)
            {
                _lastClickedTgDoc = doc;
                if (!GridTg.SelectedItems.Contains(doc))
                {
                    GridTg.SelectedItem = doc;
                }
            }
        }
    }

    private void GridTg_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyTgSelectionToClipboard();
            e.Handled = true;
        }
    }

    private void TgMenuCopyCell_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastClickedTgCellText))
        {
            Clipboard.SetText(_lastClickedTgCellText);
            TxtStatus.Text = $"📋 Скопировано значение ячейки: {_lastClickedTgCellText}";
        }
        else if (GridTg.SelectedItem is TechLogDoc doc)
        {
            Clipboard.SetText(doc.Context ?? doc.Event ?? "");
            TxtStatus.Text = "📋 Скопировано содержимое записи.";
        }
    }

    private void TgMenuCopyRowText_Click(object sender, RoutedEventArgs e)
    {
        CopyTgSelectionToClipboard();
    }

    private void TgMenuCopyRowJson_Click(object sender, RoutedEventArgs e)
    {
        var items = GridTg.SelectedItems.Cast<TechLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedTgDoc != null) items.Add(_lastClickedTgDoc);
        if (items.Count == 0) return;

        var json = items.Count == 1
            ? JsonSerializer.Serialize(items[0], PrettyJson)
            : JsonSerializer.Serialize(items, PrettyJson);
        Clipboard.SetText(json);
        TxtStatus.Text = $"📦 Скопировано {items.Count} записей ТЖ в формате JSON.";
    }

    private void TgMenuCopyContextSql_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedTgDoc ?? GridTg.SelectedItem as TechLogDoc;
        if (doc == null) return;
        var text = !string.IsNullOrWhiteSpace(doc.Context) ? doc.Context : doc.Sql ?? doc.Descr ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            TxtStatus.Text = "🔍 Скопирован контекст / SQL запрос записи.";
        }
    }

    private void TgMenuInspectRow_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedTgDoc ?? GridTg.SelectedItem as TechLogDoc;
        if (doc != null)
        {
            ShowDocDetails(doc);
        }
    }

    private void CopyTgSelectionToClipboard()
    {
        var items = GridTg.SelectedItems.Cast<TechLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedTgDoc != null) items.Add(_lastClickedTgDoc);
        if (items.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var doc in items)
        {
            sb.AppendLine($"{doc.DateFormatted}\t{doc.Event}\t{doc.ExecutionStatus}\t{doc.DurationFormatted}\t{doc.ProcessName}\t{doc.ProcessId}\t{doc.Spid}\t{doc.OSThread}\t{doc.User}\t{doc.App}\t{doc.Context}");
        }
        Clipboard.SetText(sb.ToString().TrimEnd());
        TxtStatus.Text = $"📑 Скопировано {items.Count} строк ТЖ в буфер обмена.";
    }

    private void GridLg_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep)
        {
            var cell = FindVisualParent<DataGridCell>(dep);
            if (cell != null)
            {
                if (cell.Content is TextBlock tb)
                {
                    _lastClickedLgCellText = tb.Text;
                }
                else if (cell.Content is ContentPresenter cp && cp.Content != null)
                {
                    _lastClickedLgCellText = cp.Content.ToString() ?? "";
                }
            }

            var row = FindVisualParent<DataGridRow>(dep);
            if (row != null && row.Item is EventLogDoc doc)
            {
                _lastClickedLgDoc = doc;
                if (!GridLg.SelectedItems.Contains(doc))
                {
                    GridLg.SelectedItem = doc;
                }
            }
        }
    }

    private void GridLg_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelectedLgRows();
            e.Handled = true;
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopyLgSelectionToClipboard();
            e.Handled = true;
        }
    }

    private void BtnDeleteSelectedLg_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedLgRows();
    }

    private void LgMenuDeleteRows_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedLgRows();
    }

    /// <summary>
    /// Построчное удаление одного или нескольких выделенных событий из таблицы ЖР с обязательным подтверждением.
    /// </summary>
    private void DeleteSelectedLgRows()
    {
        var selected = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        if (selected.Count == 0 && _lastClickedLgDoc != null)
        {
            selected.Add(_lastClickedLgDoc);
        }

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Выберите одну или несколько строк в таблице для удаления.", "Удаление записей", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var count = selected.Count;
        var message = count == 1
            ? $"Вы действительно хотите удалить выбранную запись события ({selected[0].Event ?? "Событие"} от {selected[0].DateFormatted}) из таблицы?"
            : $"Вы действительно хотите удалить {count} выделенных записей из таблицы?";

        var confirm = MessageBox.Show(
            this,
            message,
            "Подтверждение удаления",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes)
            return;

        if (count == 1)
        {
            _eventLogDocs.Remove(selected[0]);
        }
        else
        {
            var ids = new HashSet<string>(selected.Select(d => d.Id));
            _eventLogDocs.RemoveAll(d => ids.Contains(d.Id));
        }

        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
        UpdateLgFilterChips();
        TxtStatus.Text = $"🗑️ Удалено записей из таблицы: {count}. Осталось в таблице: {_eventLogDocs.Count}.";
    }

    private void LgMenuCopyCell_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastClickedLgCellText))
        {
            Clipboard.SetText(_lastClickedLgCellText);
            TxtStatus.Text = $"📋 Скопировано значение ячейки: {_lastClickedLgCellText}";
        }
        else if (GridLg.SelectedItem is EventLogDoc doc)
        {
            Clipboard.SetText(doc.Comment ?? doc.Event ?? "");
            TxtStatus.Text = "📋 Скопировано содержимое записи ЖР.";
        }
    }

    private void LgMenuCopyRowText_Click(object sender, RoutedEventArgs e)
    {
        CopyLgSelectionToClipboard();
    }

    private void LgMenuCopyRowJson_Click(object sender, RoutedEventArgs e)
    {
        var items = GridLg.SelectedItems.Cast<EventLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedLgDoc != null) items.Add(_lastClickedLgDoc);
        if (items.Count == 0) return;

        var json = items.Count == 1
            ? JsonSerializer.Serialize(items[0], PrettyJson)
            : JsonSerializer.Serialize(items, PrettyJson);
        Clipboard.SetText(json);
        TxtStatus.Text = $"📦 Скопировано {items.Count} записей ЖР в формате JSON.";
    }

    private void LgMenuCopyComment_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedLgDoc ?? GridLg.SelectedItem as EventLogDoc;
        if (doc == null) return;
        var text = doc.Comment ?? doc.Meta ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            TxtStatus.Text = "💬 Скопирован комментарий записи ЖР.";
        }
    }

    private void LgMenuInspectRow_Click(object sender, RoutedEventArgs e)
    {
        var doc = _lastClickedLgDoc ?? GridLg.SelectedItem as EventLogDoc;
        if (doc != null)
        {
            ShowDocDetails(doc);
        }
    }

    private void CopyLgSelectionToClipboard()
    {
        var items = GridLg.SelectedItems.Cast<EventLogDoc>().ToList();
        if (items.Count == 0 && _lastClickedLgDoc != null) items.Add(_lastClickedLgDoc);
        if (items.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var doc in items)
        {
            sb.AppendLine($"{doc.DateFormatted}\t{doc.Event}\t{doc.Importance}\t{doc.User}\t{doc.Computer}\t{doc.App}\t{doc.Meta}\t{doc.DataPresentation}\t{doc.Server}\t{doc.Session}\t{doc.FileName}\t{doc.FileSizeFormatted}\t{doc.Comment}");
        }
        Clipboard.SetText(sb.ToString().TrimEnd());
        TxtStatus.Text = $"📑 Скопировано {items.Count} строк ЖР в буфер обмена.";
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindVisualParent<T>(parentObject);
    }

    #endregion

    #region Активные Фильтры (Чипы / Filter Badges)

    private readonly struct FilterChipModel
    {
        public string Text { get; }
        public bool IsNegative { get; }
        public Action OnRemove { get; }

        public FilterChipModel(string text, bool isNegative, Action onRemove)
        {
            Text = text;
            IsNegative = isNegative;
            OnRemove = onRemove;
        }
    }

    private void UpdateTgFilterChips()
    {
        if (ContainerTgFilterChips == null) return;
        var chips = new List<FilterChipModel>();

        var query = TxtTgFilter?.Text ?? "";
        var rawTokens = query.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawToken in rawTokens)
        {
            var token = rawToken.Trim().Trim(',', ';', '"', '\'');
            if (token.Length == 0) continue;

            bool isNegative = token.StartsWith('!') || token.StartsWith('-') ||
                              token.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase) ||
                              token.StartsWith("НЕ:", StringComparison.OrdinalIgnoreCase);

            chips.Add(new FilterChipModel(
                text: isNegative ? $"НЕ: {token}" : token,
                isNegative: isNegative,
                onRemove: () =>
                {
                    RemoveSearchToken(TxtTgFilter, rawToken);
                }));
        }

        // Фильтр времени
        if (_tgHasTimeFrom || _tgHasTimeTo)
        {
            var from = TxtTgTimeFrom?.Text?.Trim() ?? "";
            var to = TxtTgTimeTo?.Text?.Trim() ?? "";
            var timeDesc = !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to)
                ? $"{from} — {to}"
                : !string.IsNullOrEmpty(from) ? $"с {from}" : $"по {to}";

            chips.Add(new FilterChipModel(timeDesc, isNegative: false, onRemove: () =>
            {
                if (TxtTgTimeFrom != null) TxtTgTimeFrom.Text = "";
                if (TxtTgTimeTo != null) TxtTgTimeTo.Text = "";
            }));
        }

        // Фильтр даты календаря
        if (_tgHasDateFilter)
        {
            var dateDesc = _tgFilterDateFrom.HasValue && _tgFilterDateTo.HasValue
                ? (_tgFilterDateFrom.Value == _tgFilterDateTo.Value ? $"📅 {_tgFilterDateFrom:dd.MM.yyyy}" : $"📅 {_tgFilterDateFrom:dd.MM.yyyy} — {_tgFilterDateTo:dd.MM.yyyy}")
                : _tgFilterDateFrom.HasValue ? $"📅 с {_tgFilterDateFrom:dd.MM.yyyy}" : $"📅 по {_tgFilterDateTo:dd.MM.yyyy}";

            chips.Add(new FilterChipModel(dateDesc, isNegative: false, onRemove: () =>
            {
                ClearTgDateFilter();
            }));
        }

        // Включающее событие (ComboBox)
        if (CmbTgEventFilter?.SelectedIndex > 0)
        {
            var selectedText = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var tag = (CmbTgEventFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? selectedText;
            var shortLabel = tag.Length > 25 ? tag[..25] + "..." : tag;
            chips.Add(new FilterChipModel(shortLabel, isNegative: false, onRemove: () =>
            {
                CmbTgEventFilter.SelectedIndex = 0;
            }));
        }

        // Включающие статусы (чекбоксы)
        if (ChkTgIncRunning?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Выполняются", isNegative: false, onRemove: () =>
            {
                ChkTgIncRunning.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgIncCompleted?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Завершенные", isNegative: false, onRemove: () =>
            {
                ChkTgIncCompleted.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Мин длительность
        if (CmbTgMinDuration?.SelectedIndex > 0)
        {
            var durText = (CmbTgMinDuration.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            chips.Add(new FilterChipModel(durText, isNegative: false, onRemove: () =>
            {
                CmbTgMinDuration.SelectedIndex = 0;
            }));
        }

        // Исключающие события (множественный выбор чекбоксами)
        foreach (var exItem in _tgExcludeEventItems.Where(x => x.IsChecked))
        {
            var target = exItem;
            chips.Add(new FilterChipModel($"НЕ: {target.Tag}", isNegative: true, onRemove: () =>
            {
                target.IsChecked = false;
                ChkTgExcludeEventItem_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие процессы (чекбоксы)
        if (ChkTgExRphost?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: rphost", isNegative: true, onRemove: () =>
            {
                ChkTgExRphost.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgExRmngr?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: rmngr", isNegative: true, onRemove: () =>
            {
                ChkTgExRmngr.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgExRagent?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: ragent", isNegative: true, onRemove: () =>
            {
                ChkTgExRagent.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие статусы
        if (ChkTgExCompleted?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Завершенные", isNegative: true, onRemove: () =>
            {
                ChkTgExCompleted.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkTgExRunning?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Выполняющиеся", isNegative: true, onRemove: () =>
            {
                ChkTgExRunning.IsChecked = false;
                ChkTgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Чипы выбранных полей ТЖ (Пользователи, Приложения, PID, SPID, OSThread)
        CollectFieldFilterChips(_tgUserItems, chips, true);
        CollectFieldFilterChips(_tgAppItems, chips, true);
        CollectFieldFilterChips(_tgPidItems, chips, true);
        CollectFieldFilterChips(_tgSpidItems, chips, true);
        CollectFieldFilterChips(_tgThreadItems, chips, true);

        RenderFilterChips(chips, PanelTgPopupChips, BtnTgMoreChips, ContainerTgFilterChips);
    }

    private void CollectFieldFilterChips(ObservableCollection<FieldValueItem> collection, List<FilterChipModel> chips, bool isTg)
    {
        foreach (var item in collection.Where(x => x.IsInclude || x.IsExclude))
        {
            var target = item;
            var label = string.IsNullOrEmpty(target.Value) ? "<Пусто>" : target.Value;
            var shortLabel = label.Length > 22 ? label[..22] + "..." : label;
            bool isEx = target.IsExclude;
            var chipText = isEx ? $"НЕ {target.Category}: {shortLabel}" : $"{target.Category}: {shortLabel}";

            chips.Add(new FilterChipModel(chipText, isNegative: isEx, onRemove: () =>
            {
                target.IsInclude = false;
                target.IsExclude = false;
                if (isTg)
                {
                    UpdateTgFilterChips();
                    _tgCollectionView?.Refresh();
                    UpdateTgCountStatus();
                }
                else
                {
                    UpdateLgFilterChips();
                    _lgCollectionView?.Refresh();
                    UpdateLgCountStatus();
                }
            }));
        }
    }

    private void UpdateLgFilterChips()
    {
        if (ContainerLgFilterChips == null) return;
        var chips = new List<FilterChipModel>();

        var query = TxtLgFilter?.Text ?? "";
        var rawTokens = query.Split(SearchSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawToken in rawTokens)
        {
            var token = rawToken.Trim().Trim(',', ';', '"', '\'');
            if (token.Length == 0) continue;

            bool isNegative = token.StartsWith('!') || token.StartsWith('-') ||
                              token.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase) ||
                              token.StartsWith("НЕ:", StringComparison.OrdinalIgnoreCase);

            chips.Add(new FilterChipModel(
                text: isNegative ? $"НЕ: {token}" : token,
                isNegative: isNegative,
                onRemove: () =>
                {
                    RemoveSearchToken(TxtLgFilter, rawToken);
                }));
        }

        if (_lgHasTimeFrom || _lgHasTimeTo)
        {
            var from = TxtLgTimeFrom?.Text?.Trim() ?? "";
            var to = TxtLgTimeTo?.Text?.Trim() ?? "";
            var timeDesc = !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to)
                ? $"{from} — {to}"
                : !string.IsNullOrEmpty(from) ? $"с {from}" : $"по {to}";

            chips.Add(new FilterChipModel(timeDesc, isNegative: false, onRemove: () =>
            {
                if (TxtLgTimeFrom != null) TxtLgTimeFrom.Text = "";
                if (TxtLgTimeTo != null) TxtLgTimeTo.Text = "";
            }));
        }

        // Фильтр даты календаря
        if (_lgHasDateFilter)
        {
            var dateDesc = _lgFilterDateFrom.HasValue && _lgFilterDateTo.HasValue
                ? (_lgFilterDateFrom.Value == _lgFilterDateTo.Value ? $"📅 {_lgFilterDateFrom:dd.MM.yyyy}" : $"📅 {_lgFilterDateFrom:dd.MM.yyyy} — {_lgFilterDateTo:dd.MM.yyyy}")
                : _lgFilterDateFrom.HasValue ? $"📅 с {_lgFilterDateFrom:dd.MM.yyyy}" : $"📅 по {_lgFilterDateTo:dd.MM.yyyy}";

            chips.Add(new FilterChipModel(dateDesc, isNegative: false, onRemove: () =>
            {
                ClearLgDateFilter();
            }));
        }

        if (CmbLgImportanceFilter?.SelectedIndex > 0)
        {
            var selectedText = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            var tag = (CmbLgImportanceFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? selectedText;
            var shortLabel = tag.Length > 25 ? tag[..25] + "..." : tag;
            chips.Add(new FilterChipModel(shortLabel, isNegative: false, onRemove: () =>
            {
                CmbLgImportanceFilter.SelectedIndex = 0;
            }));
        }

        // Включающие уровни важности ЖР
        if (ChkLgIncError?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Ошибки", isNegative: false, onRemove: () =>
            {
                ChkLgIncError.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgIncWarn?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Предупреждения", isNegative: false, onRemove: () =>
            {
                ChkLgIncWarn.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgIncInfo?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Информация", isNegative: false, onRemove: () =>
            {
                ChkLgIncInfo.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgIncNote?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("Примечания", isNegative: false, onRemove: () =>
            {
                ChkLgIncNote.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие уровни важности ЖР
        if (ChkLgExError?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Ошибки", isNegative: true, onRemove: () =>
            {
                ChkLgExError.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgExWarn?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Предупреждения", isNegative: true, onRemove: () =>
            {
                ChkLgExWarn.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgExInfo?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Информация", isNegative: true, onRemove: () =>
            {
                ChkLgExInfo.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        if (ChkLgExNote?.IsChecked == true)
        {
            chips.Add(new FilterChipModel("НЕ: Примечания", isNegative: true, onRemove: () =>
            {
                ChkLgExNote.IsChecked = false;
                ChkLgFilter_Click(this, new RoutedEventArgs());
            }));
        }

        // Исключающие события ЖР
        foreach (var exItem in _lgExcludeEventItems.Where(x => x.IsChecked))
        {
            var target = exItem;
            chips.Add(new FilterChipModel($"НЕ: {target.Tag}", isNegative: true, onRemove: () =>
            {
                target.IsChecked = false;
                ChkLgExcludeEventItem_Click(this, new RoutedEventArgs());
            }));
        }

        // Чипы выбранных полей ЖР (Пользователи, Приложения, Метаданные, События)
        CollectFieldFilterChips(_lgUserItems, chips, false);
        CollectFieldFilterChips(_lgAppItems, chips, false);
        CollectFieldFilterChips(_lgMetaItems, chips, false);
        CollectFieldFilterChips(_lgEventFieldItems, chips, false);

        RenderFilterChips(chips, PanelLgPopupChips, BtnLgMoreChips, ContainerLgFilterChips);
    }

    private static void RenderFilterChips(
        List<FilterChipModel> chips,
        Panel? popupPanel,
        ToggleButton? moreButton,
        UIElement? container)
    {
        if (container == null || popupPanel == null || moreButton == null) return;

        popupPanel.Children.Clear();

        if (chips.Count == 0)
        {
            container.Visibility = Visibility.Collapsed;
            moreButton.IsChecked = false;
            return;
        }

        container.Visibility = Visibility.Visible;
        moreButton.Content = $"АКТИВНЫЕ ФИЛЬТРЫ ({chips.Count}) ▼";

        foreach (var item in chips)
        {
            popupPanel.Children.Add(CreateFilterChip(item.Text, item.IsNegative, item.OnRemove));
        }
    }

    private static Border CreateFilterChip(string text, bool isNegative, Action onRemove)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(isNegative ? Color.FromArgb(0x2E, 0x8A, 0x2B, 0x2B) : Color.FromArgb(0x22, 0xE6, 0x6F, 0x27)),
            BorderBrush = new SolidColorBrush(isNegative ? Color.FromRgb(0x8A, 0x2B, 0x2B) : Color.FromRgb(0xE6, 0x6F, 0x27)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 2, 7, 2),
            Margin = new Thickness(0, 0, 6, 4),
            Cursor = Cursors.Hand
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(isNegative ? Color.FromRgb(0xFA, 0xCA, 0xCA) : Color.FromRgb(0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        var btnRemove = new Button
        {
            Content = "✕",
            Foreground = new SolidColorBrush(isNegative ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0xE6, 0x6F, 0x27)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Удалить фильтр"
        };
        btnRemove.Click += (_, _) => onRemove();

        sp.Children.Add(tb);
        sp.Children.Add(btnRemove);
        border.Child = sp;

        return border;
    }

    private static void RemoveSearchToken(TextBox? textBox, string tokenToRemove)
    {
        if (textBox == null || string.IsNullOrWhiteSpace(textBox.Text)) return;
        var text = textBox.Text;
        var idx = text.IndexOf(tokenToRemove, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            text = text.Remove(idx, tokenToRemove.Length).Trim();
            text = text.Replace(", ,", ",").Trim(',', ' ');
            textBox.Text = text;
        }
    }

    #endregion

    #region Экспорт в Microsoft Excel (.xlsx)

    private async void BtnExportTgExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportTgDocsToExcelAsync();
    }

    private async void TgMenuExportExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportTgDocsToExcelAsync();
    }

    private async void BtnExportLgExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportLgDocsToExcelAsync();
    }

    private async void LgMenuExportExcel_Click(object sender, RoutedEventArgs e)
    {
        await ExportLgDocsToExcelAsync();
    }

    private async Task ExportTgDocsToExcelAsync()
    {
        var selectedDocs = GridTg.SelectedItems.OfType<TechLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_tgCollectionView?.Cast<TechLogDoc>().ToList() ?? _techLogDocs);

        if (docsToExport.Count == 0)
        {
            MessageBox.Show("Нет данных Технологического Журнала (ТЖ) для экспорта в Excel! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : "table";
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ТЖ в Excel" : $"Экспорт таблицы ТЖ ({docsToExport.Count} строк) в Excel",
            Filter = "Книга Microsoft Excel (*.xlsx)|*.xlsx|Все файлы (*.*)|*.*",
            FileName = $"ones_techlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        if (saveDialog.ShowDialog() == true)
        {
            TxtStatus.Text = $"⏳ Формирование файла Excel (.xlsx) для {docsToExport.Count} записей ТЖ...";
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                var filePath = saveDialog.FileName;
                await Task.Run(() => ExcelExportService.ExportTechLogToExcel(filePath, docsToExport));

                var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                TxtStatus.Text = $"📊 Экспортировано {docsToExport.Count} записей ТЖ в Excel: {Path.GetFileName(filePath)}";

                var openResult = MessageBox.Show(
                    $"Экспорт ТЖ в Microsoft Excel успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {filePath}\n\nОткрыть созданный файл в Excel?",
                    "Экспорт в Excel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (openResult == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при формировании Excel файла ТЖ:\n{ex.Message}", "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка экспорта в Excel.";
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    private async Task ExportLgDocsToExcelAsync()
    {
        var selectedDocs = GridLg.SelectedItems.OfType<EventLogDoc>().ToList();
        var isSelected = selectedDocs.Count > 0;
        var docsToExport = isSelected
            ? selectedDocs
            : (_lgCollectionView?.Cast<EventLogDoc>().ToList() ?? _eventLogDocs);

        if (docsToExport.Count == 0)
        {
            MessageBox.Show("Нет данных Журнала Регистрации (ЖР) для экспорта в Excel! Сначала распарсьте логи или выберите строки.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var scopePrefix = isSelected ? $"selected_{docsToExport.Count}" : "table";
        var saveDialog = new SaveFileDialog
        {
            Title = isSelected ? $"Экспорт {docsToExport.Count} выделенных строк ЖР в Excel" : $"Экспорт таблицы ЖР ({docsToExport.Count} строк) в Excel",
            Filter = "Книга Microsoft Excel (*.xlsx)|*.xlsx|Все файлы (*.*)|*.*",
            FileName = $"ones_eventlog_{scopePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };

        if (saveDialog.ShowDialog() == true)
        {
            TxtStatus.Text = $"⏳ Формирование файла Excel (.xlsx) для {docsToExport.Count} записей ЖР...";
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                var filePath = saveDialog.FileName;
                await Task.Run(() => ExcelExportService.ExportEventLogToExcel(filePath, docsToExport));

                var scopeText = isSelected ? $"выделенные {docsToExport.Count} строк(и)" : $"все {docsToExport.Count} строк(и) таблицы";
                TxtStatus.Text = $"📊 Экспортировано {docsToExport.Count} записей ЖР в Excel: {Path.GetFileName(filePath)}";

                var openResult = MessageBox.Show(
                    $"Экспорт ЖР в Microsoft Excel успешно завершен!\n\n• Сохранены: {scopeText}\n• Файл: {filePath}\n\nОткрыть созданный файл в Excel?",
                    "Экспорт в Excel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (openResult == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при формировании Excel файла ЖР:\n{ex.Message}", "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "Ошибка экспорта в Excel.";
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }

    #endregion

    #region Интерактивный Календарь и Выбор Периода (ТЖ и ЖР)

    private DateTime? _tgRangeFirstClick = null;
    private DateTime? _lgRangeFirstClick = null;
    private TextBox? _tgLastFocusedDateBox = null;
    private TextBox? _lgLastFocusedDateBox = null;

    private static DateTime? ParseDateInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ||
            DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ||
            DateTime.TryParse(trimmed, out dt))
        {
            return dt.Date;
        }
        return null;
    }

    private void TxtTgDate_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            _tgLastFocusedDateBox = tb;
            if (TxtTgCalendarHint != null)
            {
                TxtTgCalendarHint.Text = tb == TxtTgDateFrom 
                    ? "Кликните на календаре дату «С»" 
                    : "Кликните на календаре дату «ПО»";
                TxtTgCalendarHint.Foreground = (Brush)FindResource("EmberDiode");
            }
        }
    }

    private void TxtLgDate_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            _lgLastFocusedDateBox = tb;
            if (TxtLgCalendarHint != null)
            {
                TxtLgCalendarHint.Text = tb == TxtLgDateFrom 
                    ? "Кликните на календаре дату «С»" 
                    : "Кликните на календаре дату «ПО»";
                TxtLgCalendarHint.Foreground = (Brush)FindResource("EmberDiode");
            }
        }
    }

    private void SetTgDateRange(DateTime? from, DateTime? to)
    {
        _isUpdatingDateControls = true;
        try
        {
            if (from.HasValue && to.HasValue && from.Value > to.Value)
            {
                (from, to) = (to, from);
            }

            _tgFilterDateFrom = from?.Date;
            _tgFilterDateTo = to?.Date;

            if (TxtTgDateFrom != null) TxtTgDateFrom.Text = _tgFilterDateFrom?.ToString("dd.MM.yyyy") ?? "";
            if (TxtTgDateTo != null) TxtTgDateTo.Text = _tgFilterDateTo?.ToString("dd.MM.yyyy") ?? "";

            if (CalTgRange != null)
            {
                CalTgRange.SelectedDates.Clear();
                if (_tgFilterDateFrom.HasValue && _tgFilterDateTo.HasValue)
                {
                    CalTgRange.SelectedDates.AddRange(_tgFilterDateFrom.Value, _tgFilterDateTo.Value);
                    CalTgRange.DisplayDate = _tgFilterDateFrom.Value;
                }
                else if (_tgFilterDateFrom.HasValue)
                {
                    CalTgRange.SelectedDates.Add(_tgFilterDateFrom.Value);
                    CalTgRange.DisplayDate = _tgFilterDateFrom.Value;
                }
                else if (_tgFilterDateTo.HasValue)
                {
                    CalTgRange.SelectedDates.Add(_tgFilterDateTo.Value);
                    CalTgRange.DisplayDate = _tgFilterDateTo.Value;
                }
            }

            if (TxtTgCalendarHint != null)
            {
                if (_tgFilterDateFrom.HasValue && _tgFilterDateTo.HasValue)
                {
                    TxtTgCalendarHint.Text = _tgFilterDateFrom.Value == _tgFilterDateTo.Value
                        ? $"Выбран 1 день: {_tgFilterDateFrom:dd.MM.yyyy}. Кликните другую дату для периода"
                        : $"Период: {_tgFilterDateFrom:dd.MM.yyyy} — {_tgFilterDateTo:dd.MM.yyyy}";
                    TxtTgCalendarHint.Foreground = (Brush)FindResource("MidAsh");
                }
                else if (_tgFilterDateFrom.HasValue)
                {
                    TxtTgCalendarHint.Text = $"Начало: {_tgFilterDateFrom:dd.MM.yyyy}. Кликните дату окончания";
                    TxtTgCalendarHint.Foreground = (Brush)FindResource("EmberDiode");
                }
                else
                {
                    TxtTgCalendarHint.Text = "Кликните дату начала и дату окончания";
                    TxtTgCalendarHint.Foreground = (Brush)FindResource("MidAsh");
                }
            }

            UpdateTgCalendarButtonVisual();
        }
        finally
        {
            _isUpdatingDateControls = false;
        }
    }

    private void UpdateTgCalendarButtonVisual()
    {
        if (BtnTgCalendarMenu == null) return;

        if (_tgHasDateFilter)
        {
            var text = _tgFilterDateFrom.HasValue && _tgFilterDateTo.HasValue
                ? (_tgFilterDateFrom.Value == _tgFilterDateTo.Value ? $"📅 {_tgFilterDateFrom:dd.MM.yyyy} ▼" : $"📅 {_tgFilterDateFrom:dd.MM} - {_tgFilterDateTo:dd.MM} ▼")
                : _tgFilterDateFrom.HasValue ? $"📅 с {_tgFilterDateFrom:dd.MM} ▼" : $"📅 по {_tgFilterDateTo:dd.MM} ▼";

            BtnTgCalendarMenu.Content = text;
            BtnTgCalendarMenu.Foreground = (Brush)FindResource("EmberDiode");
            BtnTgCalendarMenu.BorderBrush = (Brush)FindResource("EmberDiode");
        }
        else
        {
            BtnTgCalendarMenu.Content = "📅 КАЛЕНДАРЬ ▼";
            BtnTgCalendarMenu.Foreground = (Brush)FindResource("MidAsh");
            BtnTgCalendarMenu.BorderBrush = (Brush)FindResource("HairlineBorder");
        }
    }

    private void ClearTgDateFilter()
    {
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(null, null);
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void TxtTgDate_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingDateControls) return;

        var from = ParseDateInput(TxtTgDateFrom?.Text);
        var to = ParseDateInput(TxtTgDateTo?.Text);

        _tgFilterDateFrom = from;
        _tgFilterDateTo = to;
        _tgRangeFirstClick = null;

        _isUpdatingDateControls = true;
        try
        {
            if (CalTgRange != null)
            {
                CalTgRange.SelectedDates.Clear();
                if (from.HasValue && to.HasValue)
                {
                    var (min, max) = from.Value <= to.Value ? (from.Value, to.Value) : (to.Value, from.Value);
                    CalTgRange.SelectedDates.AddRange(min, max);
                    CalTgRange.DisplayDate = min;
                }
                else if (from.HasValue)
                {
                    CalTgRange.SelectedDates.Add(from.Value);
                    CalTgRange.DisplayDate = from.Value;
                }
                else if (to.HasValue)
                {
                    CalTgRange.SelectedDates.Add(to.Value);
                    CalTgRange.DisplayDate = to.Value;
                }
            }
            UpdateTgCalendarButtonVisual();
        }
        finally
        {
            _isUpdatingDateControls = false;
        }
    }

    private void Calendar_GotMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource is UIElement element && 
            (element is System.Windows.Controls.Primitives.CalendarDayButton || 
             element is System.Windows.Controls.Primitives.CalendarButton ||
             element is System.Windows.Controls.Primitives.CalendarItem))
        {
            element.ReleaseMouseCapture();
        }
    }

    private static DateTime? GetDateFromDayButton(System.Windows.Controls.Primitives.CalendarDayButton dayButton, System.Windows.Controls.Calendar calendar)
    {
        if (dayButton.DataContext is DateTime dt)
        {
            return dt.Date;
        }

        var content = dayButton.Content?.ToString();
        if (!string.IsNullOrEmpty(content) && int.TryParse(content, out var dayNum))
        {
            try
            {
                var displayDate = calendar.DisplayDate;
                return new DateTime(displayDate.Year, displayDate.Month, dayNum);
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private void CalTgRange_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var dayButton = FindVisualParent<System.Windows.Controls.Primitives.CalendarDayButton>(e.OriginalSource as DependencyObject);
        if (dayButton == null) return;

        var clickedDate = GetDateFromDayButton(dayButton, CalTgRange);
        if (!clickedDate.HasValue) return;

        var date = clickedDate.Value.Date;

        // Обработка клика по дате через унифицированный метод
        e.Handled = true;
        HandleTgCalendarDateClick(date);
    }

    private void HandleTgCalendarDateClick(DateTime date)
    {
        // Если пользователь явно фокусил конкретное поле даты
        if (_tgLastFocusedDateBox == TxtTgDateFrom)
        {
            _tgLastFocusedDateBox = null;
            _tgRangeFirstClick = null;
            SetTgDateRange(date, _tgFilterDateTo);
            return;
        }
        if (_tgLastFocusedDateBox == TxtTgDateTo)
        {
            _tgLastFocusedDateBox = null;
            _tgRangeFirstClick = null;
            SetTgDateRange(_tgFilterDateFrom, date);
            return;
        }

        if (_tgRangeFirstClick == null || _tgRangeFirstClick.Value == date)
        {
            // 1-й клик (или клик по той же дате): сразу фиксируем ОДИН день (From = To = date)
            _tgRangeFirstClick = date;
            SetTgDateRange(date, date);
        }
        else
        {
            // 2-й клик по другой дате: формируем интервал [start, end]
            var start = _tgRangeFirstClick.Value;
            var end = date;
            _tgRangeFirstClick = null;

            if (end < start)
            {
                (start, end) = (end, start);
            }

            SetTgDateRange(start, end);
        }
    }

    private void CalTgRange_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingDateControls) return;
        if (CalTgRange == null || e.AddedItems == null || e.AddedItems.Count == 0 || e.AddedItems[0] is not DateTime dt) return;

        HandleTgCalendarDateClick(dt.Date);
    }

    private void BtnTgCalendarApply_Click(object sender, RoutedEventArgs e)
    {
        var from = ParseDateInput(TxtTgDateFrom?.Text);
        var to = ParseDateInput(TxtTgDateTo?.Text);

        // Если указана только одна граница диапазона — считаем, что выбран один конкретный день
        if (from.HasValue && !to.HasValue) to = from;
        else if (!from.HasValue && to.HasValue) from = to;

        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(from, to);

        if (BtnTgCalendarMenu != null) BtnTgCalendarMenu.IsChecked = false;
        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private void BtnTgCalendarClear_Click(object sender, RoutedEventArgs e)
    {
        ClearTgDateFilter();
    }

    private void BtnTgCalendarClose_Click(object sender, RoutedEventArgs e)
    {
        if (BtnTgCalendarMenu != null) BtnTgCalendarMenu.IsChecked = false;
    }

    private void BtnTgPresetToday_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(today, today);
        BtnTgCalendarApply_Click(sender, e);
    }

    private void BtnTgPresetYesterday_Click(object sender, RoutedEventArgs e)
    {
        var y = DateTime.Today.AddDays(-1);
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(y, y);
        BtnTgCalendarApply_Click(sender, e);
    }

    private void BtnTgPreset3Days_Click(object sender, RoutedEventArgs e)
    {
        var end = DateTime.Today;
        var start = end.AddDays(-2);
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(start, end);
        BtnTgCalendarApply_Click(sender, e);
    }

    private void BtnTgPreset7Days_Click(object sender, RoutedEventArgs e)
    {
        var end = DateTime.Today;
        var start = end.AddDays(-6);
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(start, end);
        BtnTgCalendarApply_Click(sender, e);
    }

    private void BtnTgPreset30Days_Click(object sender, RoutedEventArgs e)
    {
        var end = DateTime.Today;
        var start = end.AddDays(-29);
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(start, end);
        BtnTgCalendarApply_Click(sender, e);
    }

    private void BtnTgPresetThisMonth_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var start = new DateTime(today.Year, today.Month, 1);
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(start, today);
        BtnTgCalendarApply_Click(sender, e);
    }

    private void BtnTgPresetAllLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_techLogDocs.Count == 0)
        {
            MessageBox.Show("Данные Технологического Журнала пока не загружены в таблицу.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var min = _techLogDocs.Min(x => x.Date).Date;
        var max = _techLogDocs.Max(x => x.Date).Date;
        _tgRangeFirstClick = null;
        _tgLastFocusedDateBox = null;
        SetTgDateRange(min, max);
        BtnTgCalendarApply_Click(sender, e);
    }

    private void SetLgDateRange(DateTime? from, DateTime? to)
    {
        _isUpdatingDateControls = true;
        try
        {
            if (from.HasValue && to.HasValue && from.Value > to.Value)
            {
                (from, to) = (to, from);
            }

            _lgFilterDateFrom = from?.Date;
            _lgFilterDateTo = to?.Date;

            if (TxtLgDateFrom != null) TxtLgDateFrom.Text = _lgFilterDateFrom?.ToString("dd.MM.yyyy") ?? "";
            if (TxtLgDateTo != null) TxtLgDateTo.Text = _lgFilterDateTo?.ToString("dd.MM.yyyy") ?? "";

            if (CalLgRange != null)
            {
                CalLgRange.SelectedDates.Clear();
                if (_lgFilterDateFrom.HasValue && _lgFilterDateTo.HasValue)
                {
                    CalLgRange.SelectedDates.AddRange(_lgFilterDateFrom.Value, _lgFilterDateTo.Value);
                    CalLgRange.DisplayDate = _lgFilterDateFrom.Value;
                }
                else if (_lgFilterDateFrom.HasValue)
                {
                    CalLgRange.SelectedDates.Add(_lgFilterDateFrom.Value);
                    CalLgRange.DisplayDate = _lgFilterDateFrom.Value;
                }
                else if (_lgFilterDateTo.HasValue)
                {
                    CalLgRange.SelectedDates.Add(_lgFilterDateTo.Value);
                    CalLgRange.DisplayDate = _lgFilterDateTo.Value;
                }
            }

            if (TxtLgCalendarHint != null)
            {
                if (_lgFilterDateFrom.HasValue && _lgFilterDateTo.HasValue)
                {
                    TxtLgCalendarHint.Text = _lgFilterDateFrom.Value == _lgFilterDateTo.Value
                        ? $"Выбран 1 день: {_lgFilterDateFrom:dd.MM.yyyy}. Кликните другую дату для периода"
                        : $"Период: {_lgFilterDateFrom:dd.MM.yyyy} — {_lgFilterDateTo:dd.MM.yyyy}";
                    TxtLgCalendarHint.Foreground = (Brush)FindResource("MidAsh");
                }
                else if (_lgFilterDateFrom.HasValue)
                {
                    TxtLgCalendarHint.Text = $"Начало: {_lgFilterDateFrom:dd.MM.yyyy}. Кликните дату окончания";
                    TxtLgCalendarHint.Foreground = (Brush)FindResource("EmberDiode");
                }
                else
                {
                    TxtLgCalendarHint.Text = "Кликните дату начала и дату окончания";
                    TxtLgCalendarHint.Foreground = (Brush)FindResource("MidAsh");
                }
            }

            UpdateLgCalendarButtonVisual();
        }
        finally
        {
            _isUpdatingDateControls = false;
        }
    }

    private void UpdateLgCalendarButtonVisual()
    {
        if (BtnLgCalendarMenu == null) return;

        if (_lgHasDateFilter)
        {
            var text = _lgFilterDateFrom.HasValue && _lgFilterDateTo.HasValue
                ? (_lgFilterDateFrom.Value == _lgFilterDateTo.Value ? $"📅 {_lgFilterDateFrom:dd.MM.yyyy} ▼" : $"📅 {_lgFilterDateFrom:dd.MM} - {_lgFilterDateTo:dd.MM} ▼")
                : _lgFilterDateFrom.HasValue ? $"📅 с {_lgFilterDateFrom:dd.MM} ▼" : $"📅 по {_lgFilterDateTo:dd.MM} ▼";

            BtnLgCalendarMenu.Content = text;
            BtnLgCalendarMenu.Foreground = (Brush)FindResource("EmberDiode");
            BtnLgCalendarMenu.BorderBrush = (Brush)FindResource("EmberDiode");
        }
        else
        {
            BtnLgCalendarMenu.Content = "📅 КАЛЕНДАРЬ ▼";
            BtnLgCalendarMenu.Foreground = (Brush)FindResource("MidAsh");
            BtnLgCalendarMenu.BorderBrush = (Brush)FindResource("HairlineBorder");
        }
    }

    private void ClearLgDateFilter()
    {
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(null, null);
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void TxtLgDate_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingDateControls) return;

        var from = ParseDateInput(TxtLgDateFrom?.Text);
        var to = ParseDateInput(TxtLgDateTo?.Text);

        _lgFilterDateFrom = from;
        _lgFilterDateTo = to;
        _lgRangeFirstClick = null;

        _isUpdatingDateControls = true;
        try
        {
            if (CalLgRange != null)
            {
                CalLgRange.SelectedDates.Clear();
                if (from.HasValue && to.HasValue)
                {
                    var (min, max) = from.Value <= to.Value ? (from.Value, to.Value) : (to.Value, from.Value);
                    CalLgRange.SelectedDates.AddRange(min, max);
                    CalLgRange.DisplayDate = min;
                }
                else if (from.HasValue)
                {
                    CalLgRange.SelectedDates.Add(from.Value);
                    CalLgRange.DisplayDate = from.Value;
                }
                else if (to.HasValue)
                {
                    CalLgRange.SelectedDates.Add(to.Value);
                    CalLgRange.DisplayDate = to.Value;
                }
            }
            UpdateLgCalendarButtonVisual();
        }
        finally
        {
            _isUpdatingDateControls = false;
        }
    }

    private void CalLgRange_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        var dayButton = FindVisualParent<System.Windows.Controls.Primitives.CalendarDayButton>(e.OriginalSource as DependencyObject);
        if (dayButton == null) return;

        var clickedDate = GetDateFromDayButton(dayButton, CalLgRange);
        if (!clickedDate.HasValue) return;

        var date = clickedDate.Value.Date;

        // Обработка клика по дате через унифицированный метод
        e.Handled = true;
        HandleLgCalendarDateClick(date);
    }

    private void HandleLgCalendarDateClick(DateTime date)
    {
        // Если пользователь явно фокусил конкретное поле даты
        if (_lgLastFocusedDateBox == TxtLgDateFrom)
        {
            _lgLastFocusedDateBox = null;
            _lgRangeFirstClick = null;
            SetLgDateRange(date, _lgFilterDateTo);
            return;
        }
        if (_lgLastFocusedDateBox == TxtLgDateTo)
        {
            _lgLastFocusedDateBox = null;
            _lgRangeFirstClick = null;
            SetLgDateRange(_lgFilterDateFrom, date);
            return;
        }

        if (_lgRangeFirstClick == null || _lgRangeFirstClick.Value == date)
        {
            // 1-й клик (или клик по той же дате): сразу фиксируем ОДИН день (From = To = date)
            _lgRangeFirstClick = date;
            SetLgDateRange(date, date);
        }
        else
        {
            // 2-й клик по другой дате: формируем интервал [start, end]
            var start = _lgRangeFirstClick.Value;
            var end = date;
            _lgRangeFirstClick = null;

            if (end < start)
            {
                (start, end) = (end, start);
            }

            SetLgDateRange(start, end);
        }
    }

    private void CalLgRange_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingDateControls) return;
        if (CalLgRange == null || e.AddedItems == null || e.AddedItems.Count == 0 || e.AddedItems[0] is not DateTime dt) return;

        HandleLgCalendarDateClick(dt.Date);
    }

    private void BtnLgCalendarApply_Click(object sender, RoutedEventArgs e)
    {
        var from = ParseDateInput(TxtLgDateFrom?.Text);
        var to = ParseDateInput(TxtLgDateTo?.Text);

        // Если указана только одна граница диапазона — считаем, что выбран один конкретный день
        if (from.HasValue && !to.HasValue) to = from;
        else if (!from.HasValue && to.HasValue) from = to;

        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(from, to);

        if (BtnLgCalendarMenu != null) BtnLgCalendarMenu.IsChecked = false;
        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private void BtnLgCalendarClear_Click(object sender, RoutedEventArgs e)
    {
        ClearLgDateFilter();
    }

    private void BtnLgCalendarClose_Click(object sender, RoutedEventArgs e)
    {
        if (BtnLgCalendarMenu != null) BtnLgCalendarMenu.IsChecked = false;
    }

    private void BtnLgPresetToday_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(today, today);
        BtnLgCalendarApply_Click(sender, e);
    }

    private void BtnLgPresetYesterday_Click(object sender, RoutedEventArgs e)
    {
        var y = DateTime.Today.AddDays(-1);
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(y, y);
        BtnLgCalendarApply_Click(sender, e);
    }

    private void BtnLgPreset3Days_Click(object sender, RoutedEventArgs e)
    {
        var end = DateTime.Today;
        var start = end.AddDays(-2);
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(start, end);
        BtnLgCalendarApply_Click(sender, e);
    }

    private void BtnLgPreset7Days_Click(object sender, RoutedEventArgs e)
    {
        var end = DateTime.Today;
        var start = end.AddDays(-6);
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(start, end);
        BtnLgCalendarApply_Click(sender, e);
    }

    private void BtnLgPreset30Days_Click(object sender, RoutedEventArgs e)
    {
        var end = DateTime.Today;
        var start = end.AddDays(-29);
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(start, end);
        BtnLgCalendarApply_Click(sender, e);
    }

    private void BtnLgPresetThisMonth_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        var start = new DateTime(today.Year, today.Month, 1);
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(start, today);
        BtnLgCalendarApply_Click(sender, e);
    }

    private void BtnLgPresetAllLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_eventLogDocs.Count == 0)
        {
            MessageBox.Show("Данные Журнала Регистрации пока не загружены в таблицу.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var min = _eventLogDocs.Min(x => x.Date).Date;
        var max = _eventLogDocs.Max(x => x.Date).Date;
        _lgRangeFirstClick = null;
        _lgLastFocusedDateBox = null;
        SetLgDateRange(min, max);
        BtnLgCalendarApply_Click(sender, e);
    }

    #endregion

    #region Экспорт и Импорт Профилей Фильтров и Настроек

    private TechLogFilterProfile CreateTechLogFilterProfile()
    {
        return new TechLogFilterProfile
        {
            LogPath = TxtTgPath?.Text ?? "",
            SearchText = TxtTgFilter?.Text ?? "",
            TimeFrom = TxtTgTimeFrom?.Text ?? "",
            TimeTo = TxtTgTimeTo?.Text ?? "",
            DateFrom = _tgFilterDateFrom,
            DateTo = _tgFilterDateTo,
            EventFilterIndex = CmbTgEventFilter?.SelectedIndex ?? 0,
            IncludeRunning = ChkTgIncRunning?.IsChecked == true,
            IncludeCompleted = ChkTgIncCompleted?.IsChecked == true,
            MinDurationIndex = CmbTgMinDuration?.SelectedIndex ?? 0,
            SortPresetIndex = CmbTgSortPreset?.SelectedIndex ?? 0,
            LimitIndex = CmbTgLimit?.SelectedIndex ?? 0,
            ExcludeRphost = ChkTgExRphost?.IsChecked == true,
            ExcludeRmngr = ChkTgExRmngr?.IsChecked == true,
            ExcludeRagent = ChkTgExRagent?.IsChecked == true,
            ExcludeCompleted = ChkTgExCompleted?.IsChecked == true,
            ExcludeRunning = ChkTgExRunning?.IsChecked == true,
            ExcludeEvents = _tgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToList(),
            IncludedUsers = _tgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedUsers = _tgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
            IncludedApps = _tgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedApps = _tgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
            IncludedPids = _tgPidItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedPids = _tgPidItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
            IncludedSpids = _tgSpidItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedSpids = _tgSpidItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
            IncludedThreads = _tgThreadItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedThreads = _tgThreadItems.Where(x => x.IsExclude).Select(x => x.Value).ToList()
        };
    }

    private void ApplyTechLogFilterProfile(TechLogFilterProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.LogPath) && TxtTgPath != null) TxtTgPath.Text = p.LogPath;
        if (TxtTgFilter != null && p.SearchText != null) TxtTgFilter.Text = p.SearchText;
        if (TxtTgTimeFrom != null && p.TimeFrom != null) TxtTgTimeFrom.Text = p.TimeFrom;
        if (TxtTgTimeTo != null && p.TimeTo != null) TxtTgTimeTo.Text = p.TimeTo;

        SetTgDateRange(p.DateFrom, p.DateTo);

        if (CmbTgEventFilter != null && p.EventFilterIndex >= 0 && p.EventFilterIndex < CmbTgEventFilter.Items.Count)
            CmbTgEventFilter.SelectedIndex = p.EventFilterIndex;

        if (ChkTgIncRunning != null) ChkTgIncRunning.IsChecked = p.IncludeRunning;
        if (ChkTgIncCompleted != null) ChkTgIncCompleted.IsChecked = p.IncludeCompleted;
        if (ChkTgExRphost != null) ChkTgExRphost.IsChecked = p.ExcludeRphost;
        if (ChkTgExRmngr != null) ChkTgExRmngr.IsChecked = p.ExcludeRmngr;
        if (ChkTgExRagent != null) ChkTgExRagent.IsChecked = p.ExcludeRagent;
        if (ChkTgExCompleted != null) ChkTgExCompleted.IsChecked = p.ExcludeCompleted;
        if (ChkTgExRunning != null) ChkTgExRunning.IsChecked = p.ExcludeRunning;

        if (CmbTgMinDuration != null && p.MinDurationIndex >= 0 && p.MinDurationIndex < CmbTgMinDuration.Items.Count)
            CmbTgMinDuration.SelectedIndex = p.MinDurationIndex;
        if (CmbTgSortPreset != null && p.SortPresetIndex >= 0 && p.SortPresetIndex < CmbTgSortPreset.Items.Count)
            CmbTgSortPreset.SelectedIndex = p.SortPresetIndex;
        if (CmbTgLimit != null && p.LimitIndex >= 0 && p.LimitIndex < CmbTgLimit.Items.Count)
            CmbTgLimit.SelectedIndex = p.LimitIndex;

        if (p.ExcludeEvents != null)
        {
            var set = p.ExcludeEvents.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _tgExcludeEventItems)
            {
                item.IsChecked = set.Contains(item.Tag);
            }
        }

        RestoreFieldSelections(p.IncludedUsers, p.ExcludedUsers, _tgUserItems);
        RestoreFieldSelections(p.IncludedApps, p.ExcludedApps, _tgAppItems);
        RestoreFieldSelections(p.IncludedPids, p.ExcludedPids, _tgPidItems);
        RestoreFieldSelections(p.IncludedSpids, p.ExcludedSpids, _tgSpidItems);
        RestoreFieldSelections(p.IncludedThreads, p.ExcludedThreads, _tgThreadItems);

        UpdateTgFilterChips();
        _tgCollectionView?.Refresh();
        UpdateTgCountStatus();
    }

    private EventLogFilterProfile CreateEventLogFilterProfile()
    {
        return new EventLogFilterProfile
        {
            LogPath = TxtLgPath?.Text ?? "",
            SearchText = TxtLgFilter?.Text ?? "",
            TimeFrom = TxtLgTimeFrom?.Text ?? "",
            TimeTo = TxtLgTimeTo?.Text ?? "",
            DateFrom = _lgFilterDateFrom,
            DateTo = _lgFilterDateTo,
            ImportanceIndex = CmbLgImportanceFilter?.SelectedIndex ?? 0,
            IncludeError = ChkLgIncError?.IsChecked == true,
            IncludeWarn = ChkLgIncWarn?.IsChecked == true,
            IncludeInfo = ChkLgIncInfo?.IsChecked == true,
            IncludeNote = ChkLgIncNote?.IsChecked == true,
            SortPresetIndex = CmbLgSortPreset?.SelectedIndex ?? 0,
            LimitIndex = CmbLgLimit?.SelectedIndex ?? 0,
            ExcludeError = ChkLgExError?.IsChecked == true,
            ExcludeWarn = ChkLgExWarn?.IsChecked == true,
            ExcludeInfo = ChkLgExInfo?.IsChecked == true,
            ExcludeNote = ChkLgExNote?.IsChecked == true,
            ExcludeEvents = _lgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToList(),
            IncludedUsers = _lgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedUsers = _lgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
            IncludedApps = _lgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedApps = _lgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
            IncludedMetas = _lgMetaItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedMetas = _lgMetaItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
            IncludedEvents = _lgEventFieldItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
            ExcludedEvents = _lgEventFieldItems.Where(x => x.IsExclude).Select(x => x.Value).ToList()
        };
    }

    private void ApplyEventLogFilterProfile(EventLogFilterProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.LogPath) && TxtLgPath != null) TxtLgPath.Text = p.LogPath;
        if (TxtLgFilter != null && p.SearchText != null) TxtLgFilter.Text = p.SearchText;
        if (TxtLgTimeFrom != null && p.TimeFrom != null) TxtLgTimeFrom.Text = p.TimeFrom;
        if (TxtLgTimeTo != null && p.TimeTo != null) TxtLgTimeTo.Text = p.TimeTo;

        SetLgDateRange(p.DateFrom, p.DateTo);

        if (CmbLgImportanceFilter != null && p.ImportanceIndex >= 0 && p.ImportanceIndex < CmbLgImportanceFilter.Items.Count)
            CmbLgImportanceFilter.SelectedIndex = p.ImportanceIndex;

        if (ChkLgIncError != null) ChkLgIncError.IsChecked = p.IncludeError;
        if (ChkLgIncWarn != null) ChkLgIncWarn.IsChecked = p.IncludeWarn;
        if (ChkLgIncInfo != null) ChkLgIncInfo.IsChecked = p.IncludeInfo;
        if (ChkLgIncNote != null) ChkLgIncNote.IsChecked = p.IncludeNote;

        if (ChkLgExError != null) ChkLgExError.IsChecked = p.ExcludeError;
        if (ChkLgExWarn != null) ChkLgExWarn.IsChecked = p.ExcludeWarn;
        if (ChkLgExInfo != null) ChkLgExInfo.IsChecked = p.ExcludeInfo;
        if (ChkLgExNote != null) ChkLgExNote.IsChecked = p.ExcludeNote;

        if (p.ExcludeEvents != null)
        {
            var set = p.ExcludeEvents.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _lgExcludeEventItems)
            {
                item.IsChecked = set.Contains(item.Tag);
            }
        }

        if (CmbLgSortPreset != null && p.SortPresetIndex >= 0 && p.SortPresetIndex < CmbLgSortPreset.Items.Count)
            CmbLgSortPreset.SelectedIndex = p.SortPresetIndex;
        if (CmbLgLimit != null && p.LimitIndex >= 0 && p.LimitIndex < CmbLgLimit.Items.Count)
            CmbLgLimit.SelectedIndex = p.LimitIndex;

        RestoreFieldSelections(p.IncludedUsers, p.ExcludedUsers, _lgUserItems);
        RestoreFieldSelections(p.IncludedApps, p.ExcludedApps, _lgAppItems);
        RestoreFieldSelections(p.IncludedMetas, p.ExcludedMetas, _lgMetaItems);
        RestoreFieldSelections(p.IncludedEvents, p.ExcludedEvents, _lgEventFieldItems);

        UpdateLgFilterChips();
        _lgCollectionView?.Refresh();
        UpdateLgCountStatus();
    }

    private AppSettingsProfile CreateAppSettingsProfile()
    {
        return new AppSettingsProfile
        {
            TechLogPath = TxtTgPath?.Text,
            EventLogPath = TxtLgPath?.Text,
            ElasticUrl = TxtElasticUrl?.Text,
            ElasticUser = TxtElasticUser?.Text,
            ElasticApiKey = TxtElasticApiKey?.Text,
            ElasticEnabled = ChkElasticEnabled?.IsChecked ?? true,
            EventLogIndexPrefix = TxtEventLogPrefix?.Text,
            TechLogIndexPrefix = TxtTechLogPrefix?.Text,
            KibanaUrl = TxtKibanaUrl?.Text,
            DumpDirectoryTechLog = TxtDumpDirectoryTechLog?.Text,
            DumpDirectoryEventLog = TxtDumpDirectoryEventLog?.Text,
            DumpMaxTotalSizeMb = int.TryParse(TxtDumpMaxTotalSizeMb?.Text, out var mts) ? mts : 500,
            ClickHouseUrl = TxtClickHouseUrl?.Text,
            ClickHouseDb = TxtClickHouseDb?.Text,
            ClickHouseUser = TxtClickHouseUser?.Text,
            ClickHouseEnabled = ChkClickHouseEnabled?.IsChecked ?? false,
            ClickHouseTechLogTable = TxtClickHouseTechLogTable?.Text,
            ClickHouseEventLogTable = TxtClickHouseEventLogTable?.Text,
            ClickHouseBatchSize = int.TryParse(TxtClickHouseBatchSize?.Text, out var cbs) ? cbs : 5000
        };
    }

    private void ApplyAppSettingsProfile(AppSettingsProfile s)
    {
        if (!string.IsNullOrWhiteSpace(s.TechLogPath) && TxtTgPath != null) TxtTgPath.Text = s.TechLogPath;
        if (!string.IsNullOrWhiteSpace(s.EventLogPath) && TxtLgPath != null) TxtLgPath.Text = s.EventLogPath;
        if (!string.IsNullOrWhiteSpace(s.ElasticUrl) && TxtElasticUrl != null) TxtElasticUrl.Text = s.ElasticUrl;
        if (s.ElasticUser != null && TxtElasticUser != null) TxtElasticUser.Text = s.ElasticUser;
        if (s.ElasticApiKey != null && TxtElasticApiKey != null) TxtElasticApiKey.Text = s.ElasticApiKey;
        if (ChkElasticEnabled != null) ChkElasticEnabled.IsChecked = s.ElasticEnabled;
        if (!string.IsNullOrWhiteSpace(s.EventLogIndexPrefix) && TxtEventLogPrefix != null) TxtEventLogPrefix.Text = s.EventLogIndexPrefix;
        if (!string.IsNullOrWhiteSpace(s.TechLogIndexPrefix) && TxtTechLogPrefix != null) TxtTechLogPrefix.Text = s.TechLogIndexPrefix;
        if (!string.IsNullOrWhiteSpace(s.KibanaUrl) && TxtKibanaUrl != null) TxtKibanaUrl.Text = s.KibanaUrl;
        if (!string.IsNullOrWhiteSpace(s.DumpDirectoryTechLog) && TxtDumpDirectoryTechLog != null) TxtDumpDirectoryTechLog.Text = s.DumpDirectoryTechLog;
        else if (!string.IsNullOrWhiteSpace(s.DumpDirectory) && TxtDumpDirectoryTechLog != null) TxtDumpDirectoryTechLog.Text = s.DumpDirectory.TrimEnd('/', '\\') + "/techlog";
        if (!string.IsNullOrWhiteSpace(s.DumpDirectoryEventLog) && TxtDumpDirectoryEventLog != null) TxtDumpDirectoryEventLog.Text = s.DumpDirectoryEventLog;
        else if (!string.IsNullOrWhiteSpace(s.DumpDirectory) && TxtDumpDirectoryEventLog != null) TxtDumpDirectoryEventLog.Text = s.DumpDirectory.TrimEnd('/', '\\') + "/eventlog";
        if (s.DumpMaxTotalSizeMb.HasValue && TxtDumpMaxTotalSizeMb != null) TxtDumpMaxTotalSizeMb.Text = s.DumpMaxTotalSizeMb.Value.ToString();
        if (!string.IsNullOrWhiteSpace(s.ClickHouseUrl) && TxtClickHouseUrl != null) TxtClickHouseUrl.Text = s.ClickHouseUrl;
        if (!string.IsNullOrWhiteSpace(s.ClickHouseDb) && TxtClickHouseDb != null) TxtClickHouseDb.Text = s.ClickHouseDb;
        if (s.ClickHouseUser != null && TxtClickHouseUser != null) TxtClickHouseUser.Text = s.ClickHouseUser;
        if (ChkClickHouseEnabled != null) ChkClickHouseEnabled.IsChecked = s.ClickHouseEnabled;
        if (!string.IsNullOrWhiteSpace(s.ClickHouseTechLogTable) && TxtClickHouseTechLogTable != null) TxtClickHouseTechLogTable.Text = s.ClickHouseTechLogTable;
        if (!string.IsNullOrWhiteSpace(s.ClickHouseEventLogTable) && TxtClickHouseEventLogTable != null) TxtClickHouseEventLogTable.Text = s.ClickHouseEventLogTable;
        if (s.ClickHouseBatchSize > 0 && TxtClickHouseBatchSize != null) TxtClickHouseBatchSize.Text = s.ClickHouseBatchSize.ToString();
    }

    private void BtnExportProfile_Click(object sender, RoutedEventArgs e)
    {
        ExportProfileToFile(includeTg: true, includeLg: true, includeSettings: true);
    }

    private void BtnImportProfile_Click(object sender, RoutedEventArgs e)
    {
        ImportProfileFromFile();
    }

    private void BtnTgExportFilters_Click(object sender, RoutedEventArgs e)
    {
        ExportProfileToFile(includeTg: true, includeLg: false, includeSettings: false, defaultFileName: $"TechLog_Filters_{DateTime.Now:yyyy-MM-dd}.json");
    }

    private void BtnTgImportFilters_Click(object sender, RoutedEventArgs e)
    {
        ImportProfileFromFile(targetTab: "TechLog");
    }

    private void BtnLgExportFilters_Click(object sender, RoutedEventArgs e)
    {
        ExportProfileToFile(includeTg: false, includeLg: true, includeSettings: false, defaultFileName: $"EventLog_Filters_{DateTime.Now:yyyy-MM-dd}.json");
    }

    private void BtnLgImportFilters_Click(object sender, RoutedEventArgs e)
    {
        ImportProfileFromFile(targetTab: "EventLog");
    }

    private void ExportProfileToFile(bool includeTg, bool includeLg, bool includeSettings, string? defaultFileName = null)
    {
        try
        {
            var profile = new FilterProfile
            {
                Version = "2.3.0",
                ExportedAt = DateTime.Now,
                Title = includeTg && includeLg ? "Полный профиль фильтрации 1С (ТЖ + ЖР)" : (includeTg ? "Профиль фильтров Технологического Журнала" : "Профиль фильтров Журнала Регистрации"),
                Description = "Экспортировано из 1С: Log Viewer & Exporter",
                TechLog = includeTg ? CreateTechLogFilterProfile() : null,
                EventLog = includeLg ? CreateEventLogFilterProfile() : null,
                Settings = includeSettings ? CreateAppSettingsProfile() : null
            };

            var sfd = new SaveFileDialog
            {
                Filter = "JSON-профиль фильтров (*.json)|*.json|Все файлы (*.*)|*.*",
                FileName = defaultFileName ?? $"OneS_Filters_Profile_{DateTime.Now:yyyy-MM-dd}.json",
                Title = "Экспорт профиля фильтрации и настроек"
            };

            if (sfd.ShowDialog() == true)
            {
                var json = JsonSerializer.Serialize(profile, PrettyJson);
                File.WriteAllText(sfd.FileName, json, Encoding.UTF8);

                TxtStatus.Text = $"💾 Профиль фильтров успешно сохранен: {Path.GetFileName(sfd.FileName)}";
                MessageBox.Show($"Профиль успешно экспортирован в файл:\n\n{sfd.FileName}", "Экспорт завершен", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при экспорте профиля фильтрации:\n{ex.Message}", "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportProfileFromFile(string? targetTab = null)
    {
        try
        {
            var ofd = new OpenFileDialog
            {
                Filter = "JSON-профиль фильтров (*.json)|*.json|Все файлы (*.*)|*.*",
                Title = "Импорт профиля фильтрации и настроек"
            };

            if (ofd.ShowDialog() == true)
            {
                var json = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                var profile = JsonSerializer.Deserialize<FilterProfile>(json);

                if (profile == null)
                {
                    MessageBox.Show("Не удалось прочитать профиль: файл пуст или поврежден.", "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var applied = new List<string>();

                if (profile.TechLog != null && (targetTab == null || targetTab == "TechLog"))
                {
                    ApplyTechLogFilterProfile(profile.TechLog);
                    applied.Add("фильтры Технологического Журнала");
                }

                if (profile.EventLog != null && (targetTab == null || targetTab == "EventLog"))
                {
                    ApplyEventLogFilterProfile(profile.EventLog);
                    applied.Add("фильтры Журнала Регистрации");
                }

                if (profile.Settings != null && targetTab == null)
                {
                    ApplyAppSettingsProfile(profile.Settings);
                    applied.Add("системные настройки (пути/Elastic/Kibana)");
                }

                if (applied.Count == 0)
                {
                    MessageBox.Show("В выбранном файле не найдены подходящие фильтры для импорта.", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                TxtStatus.Text = $"📥 Успешно импортирован профиль: {Path.GetFileName(ofd.FileName)}";
                MessageBox.Show($"Профиль успешно загружен!\n\nПрименены:\n• {string.Join("\n• ", applied)}\n\nНазвание профиля: {profile.Title}\nДата экспорта: {profile.ExportedAt:dd.MM.yyyy HH:mm}", "Импорт завершен", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при импорте профиля фильтрации:\n{ex.Message}", "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Персистентность состояния GUI (ui_state.json)

    private static string GetGuiStateFilePath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui_state.json");
    }

    private void LoadGuiState()
    {
        try
        {
            var path = GetGuiStateFilePath();
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<GuiState>(json);
            if (state == null) return;

            if (!string.IsNullOrWhiteSpace(state.TgPath)) TxtTgPath.Text = state.TgPath;
            if (!string.IsNullOrWhiteSpace(state.LgPath)) TxtLgPath.Text = state.LgPath;

            if (state.TgFilter != null) TxtTgFilter.Text = state.TgFilter;
            if (state.TgTimeFrom != null) TxtTgTimeFrom.Text = state.TgTimeFrom;
            if (state.TgTimeTo != null) TxtTgTimeTo.Text = state.TgTimeTo;
            SetTgDateRange(state.TgDateFrom, state.TgDateTo);
            if (state.TgEventIndex >= 0 && state.TgEventIndex < CmbTgEventFilter.Items.Count)
                CmbTgEventFilter.SelectedIndex = state.TgEventIndex;

            if (state.TgExcludeEvents != null && state.TgExcludeEvents.Count > 0)
            {
                var set = state.TgExcludeEvents.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _tgExcludeEventItems)
                {
                    item.IsChecked = set.Contains(item.Tag);
                }
            }

            if (ChkTgIncRunning != null) ChkTgIncRunning.IsChecked = state.TgIncRunning;
            if (ChkTgIncCompleted != null) ChkTgIncCompleted.IsChecked = state.TgIncCompleted;

            if (ChkTgExRphost != null) ChkTgExRphost.IsChecked = state.TgExRphost;
            if (ChkTgExRmngr != null) ChkTgExRmngr.IsChecked = state.TgExRmngr;
            if (ChkTgExRagent != null) ChkTgExRagent.IsChecked = state.TgExRagent;

            if (ChkTgExCompleted != null) ChkTgExCompleted.IsChecked = state.TgExCompleted;
            if (ChkTgExRunning != null) ChkTgExRunning.IsChecked = state.TgExRunning;

            if (state.TgMinDurationIndex >= 0 && state.TgMinDurationIndex < CmbTgMinDuration.Items.Count)
                CmbTgMinDuration.SelectedIndex = state.TgMinDurationIndex;
            if (state.TgSortIndex >= 0 && state.TgSortIndex < CmbTgSortPreset.Items.Count)
                CmbTgSortPreset.SelectedIndex = state.TgSortIndex;
            if (state.TgLimitIndex == 4 || state.TgLimitIndex < 0 || state.TgLimitIndex >= CmbTgLimit.Items.Count)
                CmbTgLimit.SelectedIndex = 0;
            else
                CmbTgLimit.SelectedIndex = state.TgLimitIndex;

            if (state.TgLoadArchive.HasValue && ChkTgLoadArchive != null)
                ChkTgLoadArchive.IsChecked = state.TgLoadArchive.Value;

            RestoreFieldSelections(state.TgIncludedUsers, state.TgExcludedUsers, _tgUserItems);
            RestoreFieldSelections(state.TgIncludedApps, state.TgExcludedApps, _tgAppItems);
            RestoreFieldSelections(state.TgIncludedPids, state.TgExcludedPids, _tgPidItems);
            RestoreFieldSelections(state.TgIncludedSpids, state.TgExcludedSpids, _tgSpidItems);
            RestoreFieldSelections(state.TgIncludedThreads, state.TgExcludedThreads, _tgThreadItems);

            if (state.LgFilter != null) TxtLgFilter.Text = state.LgFilter;
            if (state.LgTimeFrom != null) TxtLgTimeFrom.Text = state.LgTimeFrom;
            if (state.LgTimeTo != null) TxtLgTimeTo.Text = state.LgTimeTo;
            SetLgDateRange(state.LgDateFrom, state.LgDateTo);
            if (state.LgImportanceIndex >= 0 && state.LgImportanceIndex < CmbLgImportanceFilter.Items.Count)
                CmbLgImportanceFilter.SelectedIndex = state.LgImportanceIndex;

            if (ChkLgIncError != null) ChkLgIncError.IsChecked = state.LgIncError;
            if (ChkLgIncWarn != null) ChkLgIncWarn.IsChecked = state.LgIncWarn;
            if (ChkLgIncInfo != null) ChkLgIncInfo.IsChecked = state.LgIncInfo;
            if (ChkLgIncNote != null) ChkLgIncNote.IsChecked = state.LgIncNote;

            if (ChkLgExError != null) ChkLgExError.IsChecked = state.LgExError;
            if (ChkLgExWarn != null) ChkLgExWarn.IsChecked = state.LgExWarn;
            if (ChkLgExInfo != null) ChkLgExInfo.IsChecked = state.LgExInfo;
            if (ChkLgExNote != null) ChkLgExNote.IsChecked = state.LgExNote;

            if (state.LgExcludeEvents != null && state.LgExcludeEvents.Count > 0)
            {
                var set = state.LgExcludeEvents.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _lgExcludeEventItems)
                {
                    item.IsChecked = set.Contains(item.Tag);
                }
            }

            if (state.LgSortIndex >= 0 && state.LgSortIndex < CmbLgSortPreset.Items.Count)
                CmbLgSortPreset.SelectedIndex = state.LgSortIndex;
            if (state.LgLimitIndex == 4 || state.LgLimitIndex < 0 || state.LgLimitIndex >= CmbLgLimit.Items.Count)
                CmbLgLimit.SelectedIndex = 0;
            else
                CmbLgLimit.SelectedIndex = state.LgLimitIndex;

            if (state.LgLoadArchive.HasValue && ChkLgLoadArchive != null)
                ChkLgLoadArchive.IsChecked = state.LgLoadArchive.Value;

            RestoreFieldSelections(state.LgIncludedUsers, state.LgExcludedUsers, _lgUserItems);
            RestoreFieldSelections(state.LgIncludedApps, state.LgExcludedApps, _lgAppItems);
            RestoreFieldSelections(state.LgIncludedMetas, state.LgExcludedMetas, _lgMetaItems);
            RestoreFieldSelections(state.LgIncludedEvents, state.LgExcludedEvents, _lgEventFieldItems);

            if (state.ElasticEnabled.HasValue && ChkElasticEnabled != null) ChkElasticEnabled.IsChecked = state.ElasticEnabled.Value;
            if (!string.IsNullOrEmpty(state.ElasticUrl) && TxtElasticUrl != null) TxtElasticUrl.Text = state.ElasticUrl;
            if (state.ElasticUser != null && TxtElasticUser != null) TxtElasticUser.Text = state.ElasticUser;
            if (state.ElasticPass != null && TxtElasticPass != null) TxtElasticPass.Password = state.ElasticPass;
            if (state.ElasticApiKey != null && TxtElasticApiKey != null) TxtElasticApiKey.Text = state.ElasticApiKey;
            if (!string.IsNullOrEmpty(state.EventLogPrefix) && TxtEventLogPrefix != null) TxtEventLogPrefix.Text = state.EventLogPrefix;
            if (!string.IsNullOrEmpty(state.TechLogPrefix) && TxtTechLogPrefix != null) TxtTechLogPrefix.Text = state.TechLogPrefix;
            if (!string.IsNullOrEmpty(state.KibanaUrl) && TxtKibanaUrl != null) TxtKibanaUrl.Text = state.KibanaUrl;
            if (!string.IsNullOrEmpty(state.DumpDirectoryTechLog) && TxtDumpDirectoryTechLog != null) TxtDumpDirectoryTechLog.Text = state.DumpDirectoryTechLog;
            else if (!string.IsNullOrEmpty(state.DumpDirectory) && TxtDumpDirectoryTechLog != null) TxtDumpDirectoryTechLog.Text = state.DumpDirectory.TrimEnd('/', '\\') + "/techlog";
            if (!string.IsNullOrEmpty(state.DumpDirectoryEventLog) && TxtDumpDirectoryEventLog != null) TxtDumpDirectoryEventLog.Text = state.DumpDirectoryEventLog;
            else if (!string.IsNullOrEmpty(state.DumpDirectory) && TxtDumpDirectoryEventLog != null) TxtDumpDirectoryEventLog.Text = state.DumpDirectory.TrimEnd('/', '\\') + "/eventlog";
            if (!string.IsNullOrEmpty(state.DumpMaxTotalSizeMb) && TxtDumpMaxTotalSizeMb != null) TxtDumpMaxTotalSizeMb.Text = state.DumpMaxTotalSizeMb;
            if (state.ClickHouseEnabled.HasValue && ChkClickHouseEnabled != null) ChkClickHouseEnabled.IsChecked = state.ClickHouseEnabled.Value;
            if (!string.IsNullOrEmpty(state.ClickHouseUrl) && TxtClickHouseUrl != null) TxtClickHouseUrl.Text = state.ClickHouseUrl;
            if (state.ClickHouseDb != null && TxtClickHouseDb != null) TxtClickHouseDb.Text = state.ClickHouseDb;
            if (state.ClickHouseUser != null && TxtClickHouseUser != null) TxtClickHouseUser.Text = state.ClickHouseUser;
            if (state.ClickHousePass != null && TxtClickHousePass != null) TxtClickHousePass.Password = state.ClickHousePass;
            if (!string.IsNullOrEmpty(state.ClickHouseTechLogTable) && TxtClickHouseTechLogTable != null) TxtClickHouseTechLogTable.Text = state.ClickHouseTechLogTable;
            if (!string.IsNullOrEmpty(state.ClickHouseEventLogTable) && TxtClickHouseEventLogTable != null) TxtClickHouseEventLogTable.Text = state.ClickHouseEventLogTable;
            if (!string.IsNullOrEmpty(state.ClickHouseBatchSize) && TxtClickHouseBatchSize != null) TxtClickHouseBatchSize.Text = state.ClickHouseBatchSize;

            // По умолчанию в программе всегда открывается вкладка Технологический журнал (ТЖ)
            MainTabControl.SelectedIndex = 0;
        }
        catch
        {
            // Устойчивость при ошибках чтения поврежденного JSON
        }
    }

    private static void RestoreFieldSelections(List<string>? incValues, List<string>? exValues, ObservableCollection<FieldValueItem> collection)
    {
        var incSet = incValues != null && incValues.Count > 0 ? incValues.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        var exSet = exValues != null && exValues.Count > 0 ? exValues.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        if (incSet != null)
        {
            foreach (var val in incSet)
            {
                var existing = collection.FirstOrDefault(x => string.Equals(x.Value, val, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsInclude = true;
                    existing.IsExclude = false;
                }
                else
                {
                    collection.Add(new FieldValueItem { Value = val, IsInclude = true, IsExclude = false });
                }
            }
        }

        if (exSet != null)
        {
            foreach (var val in exSet)
            {
                var existing = collection.FirstOrDefault(x => string.Equals(x.Value, val, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.IsExclude = true;
                    existing.IsInclude = false;
                }
                else
                {
                    collection.Add(new FieldValueItem { Value = val, IsExclude = true, IsInclude = false });
                }
            }
        }
    }

    private void SaveGuiState()
    {
        try
        {
            var state = new GuiState
            {
                TgPath = TxtTgPath.Text,
                TgFilter = TxtTgFilter.Text,
                TgTimeFrom = TxtTgTimeFrom.Text,
                TgTimeTo = TxtTgTimeTo.Text,
                TgDateFrom = _tgFilterDateFrom,
                TgDateTo = _tgFilterDateTo,
                TgEventIndex = CmbTgEventFilter.SelectedIndex,
                TgExcludeEvents = _tgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToList(),
                TgIncRunning = ChkTgIncRunning?.IsChecked == true,
                TgIncCompleted = ChkTgIncCompleted?.IsChecked == true,
                TgExRphost = ChkTgExRphost?.IsChecked == true,
                TgExRmngr = ChkTgExRmngr?.IsChecked == true,
                TgExRagent = ChkTgExRagent?.IsChecked == true,
                TgExCompleted = ChkTgExCompleted?.IsChecked == true,
                TgExRunning = ChkTgExRunning?.IsChecked == true,
                TgMinDurationIndex = CmbTgMinDuration.SelectedIndex,
                TgSortIndex = CmbTgSortPreset.SelectedIndex,
                TgLimitIndex = CmbTgLimit.SelectedIndex,
                TgLoadArchive = ChkTgLoadArchive?.IsChecked == true,
                TgIncludedUsers = _tgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedUsers = _tgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedApps = _tgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedApps = _tgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedPids = _tgPidItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedPids = _tgPidItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedSpids = _tgSpidItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedSpids = _tgSpidItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                TgIncludedThreads = _tgThreadItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                TgExcludedThreads = _tgThreadItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),

                LgPath = TxtLgPath.Text,
                LgFilter = TxtLgFilter.Text,
                LgTimeFrom = TxtLgTimeFrom.Text,
                LgTimeTo = TxtLgTimeTo.Text,
                LgDateFrom = _lgFilterDateFrom,
                LgDateTo = _lgFilterDateTo,
                LgImportanceIndex = CmbLgImportanceFilter.SelectedIndex,
                LgIncError = ChkLgIncError?.IsChecked == true,
                LgIncWarn = ChkLgIncWarn?.IsChecked == true,
                LgIncInfo = ChkLgIncInfo?.IsChecked == true,
                LgIncNote = ChkLgIncNote?.IsChecked == true,
                LgExError = ChkLgExError?.IsChecked == true,
                LgExWarn = ChkLgExWarn?.IsChecked == true,
                LgExInfo = ChkLgExInfo?.IsChecked == true,
                LgExNote = ChkLgExNote?.IsChecked == true,
                LgExcludeEvents = _lgExcludeEventItems.Where(x => x.IsChecked).Select(x => x.Tag).ToList(),
                LgSortIndex = CmbLgSortPreset.SelectedIndex,
                LgLimitIndex = CmbLgLimit.SelectedIndex,
                LgLoadArchive = ChkLgLoadArchive?.IsChecked == true,
                LgIncludedUsers = _lgUserItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedUsers = _lgUserItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                LgIncludedApps = _lgAppItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedApps = _lgAppItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                LgIncludedMetas = _lgMetaItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedMetas = _lgMetaItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),
                LgIncludedEvents = _lgEventFieldItems.Where(x => x.IsInclude).Select(x => x.Value).ToList(),
                LgExcludedEvents = _lgEventFieldItems.Where(x => x.IsExclude).Select(x => x.Value).ToList(),

                SelectedTabIndex = MainTabControl.SelectedIndex,

                ElasticEnabled = ChkElasticEnabled?.IsChecked,
                ElasticUrl = TxtElasticUrl?.Text,
                ElasticUser = TxtElasticUser?.Text,
                ElasticPass = TxtElasticPass?.Password,
                ElasticApiKey = TxtElasticApiKey?.Text,
                EventLogPrefix = TxtEventLogPrefix?.Text,
                TechLogPrefix = TxtTechLogPrefix?.Text,
                KibanaUrl = TxtKibanaUrl?.Text,
                DumpDirectoryTechLog = TxtDumpDirectoryTechLog?.Text,
                DumpDirectoryEventLog = TxtDumpDirectoryEventLog?.Text,
                DumpMaxTotalSizeMb = TxtDumpMaxTotalSizeMb?.Text,
                ClickHouseEnabled = ChkClickHouseEnabled?.IsChecked,
                ClickHouseUrl = TxtClickHouseUrl?.Text,
                ClickHouseDb = TxtClickHouseDb?.Text,
                ClickHouseUser = TxtClickHouseUser?.Text,
                ClickHousePass = TxtClickHousePass?.Password,
                ClickHouseTechLogTable = TxtClickHouseTechLogTable?.Text,
                ClickHouseEventLogTable = TxtClickHouseEventLogTable?.Text,
                ClickHouseBatchSize = TxtClickHouseBatchSize?.Text
            };

            var json = JsonSerializer.Serialize(state, PrettyJson);
            File.WriteAllText(GetGuiStateFilePath(), json);
        }
        catch
        {
            // Защита от исключений при закрытии приложения
        }
    }

    private void SaveAllStateOnClose()
    {
        try
        {
            _tgSessionCache?.Dispose();
            _lgSessionCache?.Dispose();
            SessionCacheService.CleanupAllOrphanedTempFiles();
            SaveGuiState();

            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(configPath))
            {
                var jsonString = File.ReadAllText(configPath);
                var rootNode = JsonNode.Parse(jsonString, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (rootNode is JsonObject rootObj)
                {
                    var exporterObj = rootObj["Exporter"] as JsonObject ?? rootObj;

                    if (exporterObj["TechLog"] is JsonObject tgObj && TxtTgPath != null)
                    {
                        tgObj["DirectoryPath"] = TxtTgPath.Text.Trim();
                        if (ChkTgLoadArchive != null) tgObj["LoadArchive"] = ChkTgLoadArchive.IsChecked ?? false;
                    }

                    if (exporterObj["EventLog"] is JsonObject evObj && TxtLgPath != null)
                    {
                        evObj["DirectoryPath"] = TxtLgPath.Text.Trim();
                        if (ChkLgLoadArchive != null) evObj["LoadArchive"] = ChkLgLoadArchive.IsChecked ?? false;
                    }

                    if (exporterObj["Elastic"] is JsonObject elasticObj)
                    {
                        elasticObj.Remove("Enabled");
                        if (ChkElasticEnabled != null)
                        {
                            var isElChecked = ChkElasticEnabled.IsChecked ?? false;
                            if (!isElChecked)
                            {
                                elasticObj["TechLogEnabled"] = false;
                                elasticObj["EventLogEnabled"] = false;
                            }
                            else if (!elasticObj.ContainsKey("TechLogEnabled") && !elasticObj.ContainsKey("EventLogEnabled"))
                            {
                                elasticObj["TechLogEnabled"] = false;
                                elasticObj["EventLogEnabled"] = true;
                            }
                        }
                        if (TxtElasticUrl != null) elasticObj["ServerUrl"] = TxtElasticUrl.Text.Trim();
                        if (TxtElasticUser != null) elasticObj["Username"] = TxtElasticUser.Text.Trim();
                        if (TxtElasticPass != null) elasticObj["Password"] = TxtElasticPass.Password.Trim();
                        if (TxtElasticApiKey != null) elasticObj["ApiKey"] = TxtElasticApiKey.Text.Trim();
                        if (TxtEventLogPrefix != null) elasticObj["EventLogIndexPrefix"] = TxtEventLogPrefix.Text.Trim();
                        if (TxtTechLogPrefix != null) elasticObj["TechLogIndexPrefix"] = TxtTechLogPrefix.Text.Trim();
                    }

                    if (exporterObj["Kibana"] is JsonObject kibanaObj && TxtKibanaUrl != null)
                    {
                        kibanaObj["ServerUrl"] = TxtKibanaUrl.Text.Trim();
                    }

                    var chObj = exporterObj["ClickHouse"] as JsonObject;
                    if (chObj == null)
                    {
                        chObj = new JsonObject();
                        exporterObj["ClickHouse"] = chObj;
                    }
                    chObj.Remove("Enabled");
                    if (ChkClickHouseEnabled != null)
                    {
                        var isChChecked = ChkClickHouseEnabled.IsChecked ?? false;
                        if (!isChChecked)
                        {
                            chObj["TechLogEnabled"] = false;
                            chObj["EventLogEnabled"] = false;
                        }
                        else if (!chObj.ContainsKey("TechLogEnabled") && !chObj.ContainsKey("EventLogEnabled"))
                        {
                            chObj["TechLogEnabled"] = false;
                            chObj["EventLogEnabled"] = true;
                        }
                    }
                    if (TxtClickHouseUrl != null) chObj["ServerUrl"] = TxtClickHouseUrl.Text.Trim();
                    if (TxtClickHouseDb != null) chObj["Database"] = TxtClickHouseDb.Text.Trim();
                    if (TxtClickHouseUser != null) chObj["User"] = TxtClickHouseUser.Text.Trim();
                    if (TxtClickHousePass != null) chObj["Password"] = TxtClickHousePass.Password.Trim();
                    if (TxtClickHouseTechLogTable != null) chObj["TechLogTable"] = TxtClickHouseTechLogTable.Text.Trim();
                    if (TxtClickHouseEventLogTable != null) chObj["EventLogTable"] = TxtClickHouseEventLogTable.Text.Trim();
                    if (TxtClickHouseBatchSize != null && int.TryParse(TxtClickHouseBatchSize.Text.Trim(), out var bsz))
                    {
                        chObj["BulkBatchSize"] = bsz;
                    }

                    if (exporterObj["FileDump"] is JsonObject dumpObj)
                    {
                        dumpObj.Remove("DirectoryPath");
                        if (TxtDumpDirectoryTechLog != null) dumpObj["TechLogDirectoryPath"] = TxtDumpDirectoryTechLog.Text.Trim();
                        if (TxtDumpDirectoryEventLog != null) dumpObj["EventLogDirectoryPath"] = TxtDumpDirectoryEventLog.Text.Trim();
                        if (TxtDumpMaxTotalSizeMb != null && int.TryParse(TxtDumpMaxTotalSizeMb.Text.Trim(), out var mts))
                        {
                            dumpObj["MaxTotalSizeMb"] = mts;
                        }
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                    };

                    File.WriteAllText(configPath, rootObj.ToJsonString(options), Encoding.UTF8);
                }
            }
        }
        catch
        {
            // Не блокируем завершение работы приложения
        }
    }

    #endregion
}

/// <summary>
/// Элемент отбора по конкретному значению поля таблицы (User, App, PID, SPID, OSThread, Meta, Event).
/// Поддерживает независимое включение (IsInclude) и исключение (IsExclude / НЕ).
/// </summary>
public sealed class FieldValueItem : INotifyPropertyChanged
{
    private bool _isInclude;
    private bool _isExclude;

    public string Category { get; set; } = "";
    public string Value { get; set; } = "";
    public int Count { get; set; }
    public string DisplayText => string.IsNullOrEmpty(Value) ? $"<Пусто> ({Count:N0})" : $"{Value} ({Count:N0})";

    public bool IsInclude
    {
        get => _isInclude;
        set
        {
            if (_isInclude != value)
            {
                _isInclude = value;
                if (value) _isExclude = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInclude)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExclude)));
            }
        }
    }

    public bool IsExclude
    {
        get => _isExclude;
        set
        {
            if (_isExclude != value)
            {
                _isExclude = value;
                if (value) _isInclude = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInclude)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExclude)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Элемент списка фильтра с поддержкой множественного выбора через CheckBox.
/// </summary>
public sealed class FilterCheckItem : INotifyPropertyChanged
{
    private bool _isChecked;
    public string Tag { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Модель сохраняемого локального состояния пользовательского интерфейса (ui_state.json).
/// </summary>
public sealed class GuiState
{
    public string TgPath { get; set; } = @"C:\Logs\TGLogs";
    public string TgFilter { get; set; } = "";
    public string TgTimeFrom { get; set; } = "";
    public string TgTimeTo { get; set; } = "";
    public DateTime? TgDateFrom { get; set; }
    public DateTime? TgDateTo { get; set; }
    public int TgEventIndex { get; set; } = 0;
    public List<string> TgExcludeEvents { get; set; } = [];
    public bool TgIncRunning { get; set; }
    public bool TgIncCompleted { get; set; }
    public bool TgExRphost { get; set; }
    public bool TgExRmngr { get; set; }
    public bool TgExRagent { get; set; }
    public bool TgExCompleted { get; set; }
    public bool TgExRunning { get; set; }
    public int TgMinDurationIndex { get; set; } = 0;
    public int TgSortIndex { get; set; } = 0;
    public int TgLimitIndex { get; set; } = 0; // "Все записи"
    public bool? TgLoadArchive { get; set; } = false;
    public List<string> TgIncludedUsers { get; set; } = [];
    public List<string> TgExcludedUsers { get; set; } = [];
    public List<string> TgIncludedApps { get; set; } = [];
    public List<string> TgExcludedApps { get; set; } = [];
    public List<string> TgIncludedPids { get; set; } = [];
    public List<string> TgExcludedPids { get; set; } = [];
    public List<string> TgIncludedSpids { get; set; } = [];
    public List<string> TgExcludedSpids { get; set; } = [];
    public List<string> TgIncludedThreads { get; set; } = [];
    public List<string> TgExcludedThreads { get; set; } = [];

    public string LgPath { get; set; } = @"C:\Logs\LGLogs";
    public string LgFilter { get; set; } = "";
    public string LgTimeFrom { get; set; } = "";
    public string LgTimeTo { get; set; } = "";
    public DateTime? LgDateFrom { get; set; }
    public DateTime? LgDateTo { get; set; }
    public int LgImportanceIndex { get; set; } = 0;
    public bool LgIncError { get; set; }
    public bool LgIncWarn { get; set; }
    public bool LgIncInfo { get; set; }
    public bool LgIncNote { get; set; }
    public bool LgExError { get; set; }
    public bool LgExWarn { get; set; }
    public bool LgExInfo { get; set; }
    public bool LgExNote { get; set; }
    public List<string> LgExcludeEvents { get; set; } = [];
    public int LgSortIndex { get; set; } = 0;
    public int LgLimitIndex { get; set; } = 0; // "Все записи"
    public bool? LgLoadArchive { get; set; } = false;
    public List<string> LgIncludedUsers { get; set; } = [];
    public List<string> LgExcludedUsers { get; set; } = [];
    public List<string> LgIncludedApps { get; set; } = [];
    public List<string> LgExcludedApps { get; set; } = [];
    public List<string> LgIncludedMetas { get; set; } = [];
    public List<string> LgExcludedMetas { get; set; } = [];
    public List<string> LgIncludedEvents { get; set; } = [];
    public List<string> LgExcludedEvents { get; set; } = [];

    public int SelectedTabIndex { get; set; } = 0;

    public bool? ElasticEnabled { get; set; }
    public string? ElasticUrl { get; set; }
    public string? ElasticUser { get; set; }
    public string? ElasticPass { get; set; }
    public string? ElasticApiKey { get; set; }
    public string? EventLogPrefix { get; set; }
    public string? TechLogPrefix { get; set; }
    public string? KibanaUrl { get; set; }
    public string? DumpDirectory { get; set; }
    public string? DumpDirectoryTechLog { get; set; }
    public string? DumpDirectoryEventLog { get; set; }
    public string? DumpMaxTotalSizeMb { get; set; }

    public bool? ClickHouseEnabled { get; set; }
    public string? ClickHouseUrl { get; set; }
    public string? ClickHouseDb { get; set; }
    public string? ClickHouseUser { get; set; }
    public string? ClickHousePass { get; set; }
    public string? ClickHouseTechLogTable { get; set; }
    public string? ClickHouseEventLogTable { get; set; }
    public string? ClickHouseBatchSize { get; set; }
}
