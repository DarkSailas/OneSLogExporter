using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Serialization;
using OneSLogExporter.Core.Services;
using OneSLogExporter.Core.State;
using OneSLogExporter.Service.Workers;
using Serilog;
using Serilog.Events;

var baseDir = AppContext.BaseDirectory;
Directory.SetCurrentDirectory(baseDir);

var logsDir = Path.Combine(baseDir, "logs");
Directory.CreateDirectory(logsDir);
var emergencyLogPath = Path.Combine(logsDir, "service_startup.log");

// Автоматически нормализуем Windows-слеши (\) в файлах конфигурации до старта парсера
ConfigSanitizer.SanitizeConfigFile(Path.Combine(baseDir, "appsettings.json"));
ConfigSanitizer.SanitizeConfigFile(Path.Combine(baseDir, "appsettings.local.json"));
ConfigSanitizer.SanitizeConfigFile(Path.Combine(baseDir, "appsettings.Production.json"));

// Человекочитаемое логирование: базовый уровень Information, подавление системного шума, лимит 500 МБ
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine(logsDir, "ones_exporter_.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 10,
        fileSizeLimitBytes: 52428800, // 50 МБ на файл * 10 файлов = 500 МБ максимум
        rollOnFileSizeLimit: true,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{

    var switchMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "--eventlog", "Exporter:EventLog:Enabled" },
        { "--techlog", "Exporter:TechLog:Enabled" },
        { "--filedump-ev", "Exporter:FileDump:EventLogEnabled" },
        { "--filedump-tg", "Exporter:FileDump:TechLogEnabled" },
        { "--filedump-dir", "Exporter:FileDump:DirectoryPath" },
        { "--filedump-tg-dir", "Exporter:FileDump:TechLogDirectoryPath" },
        { "--filedump-ev-dir", "Exporter:FileDump:EventLogDirectoryPath" },
        { "--filedump-limit", "Exporter:FileDump:RetainedFileCountLimit" },
        { "--filedump-max-total-mb", "Exporter:FileDump:MaxTotalSizeMb" },
        { "--es", "Exporter:Elastic:ServerUrl" },
        { "--clickhouse-ev", "Exporter:ClickHouse:EventLogEnabled" },
        { "--clickhouse-tg", "Exporter:ClickHouse:TechLogEnabled" },
        { "--ch", "Exporter:ClickHouse:ServerUrl" },
        { "--ch-url", "Exporter:ClickHouse:ServerUrl" },
        { "--ch-user", "Exporter:ClickHouse:User" },
        { "--ch-pass", "Exporter:ClickHouse:Password" },
        { "--ch-db", "Exporter:ClickHouse:Database" },
        { "--index-id", "Exporter:EventLog:IndexId" },
        { "--separation", "Exporter:Elastic:Separation" },
        { "--load-archive", "Exporter:EventLog:LoadArchive" },
        { "--database-name", "Exporter:EventLog:DatabaseName" },
        { "--db-name", "Exporter:EventLog:DatabaseName" }
    };

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = baseDir
    });

    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "OneSLogExporter";
    });

    builder.Configuration.SetBasePath(baseDir);
    builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
    builder.Configuration.AddJsonFile("appsettings.Production.json", optional: true, reloadOnChange: true);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args, switchMappings);

    builder.Services.AddSerilog((services, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logsDir, "ones_exporter_.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: 52428800, // 50 МБ на файл * 10 файлов = 500 МБ максимум
                rollOnFileSizeLimit: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");
    });

    var config = builder.Configuration.GetSection(ExporterOptions.SectionName);
    builder.Services.Configure<ExporterOptions>(config);

    var exporterOptions = config.Get<ExporterOptions>() ?? new ExporterOptions();

    builder.Services.AddSingleton(exporterOptions);
    builder.Services.AddSingleton(sp => exporterOptions.Elastic);
    builder.Services.AddSingleton(sp => exporterOptions.FileDump);
    builder.Services.AddSingleton(sp => exporterOptions.ClickHouse);
    builder.Services.AddSingleton<ElasticPublisher>();
    builder.Services.AddSingleton<ClickHousePublisher>();
    builder.Services.AddSingleton(sp => new FileDumper(exporterOptions.FileDump, sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileDumper>>(), exporterOptions));
    builder.Services.AddSingleton<JsonLogTransporter>();
    builder.Services.AddSingleton(sp => new StateTracker(Path.Combine(baseDir, exporterOptions.StateFilePath)));

    builder.Services.AddHostedService<EventLogWorker>();
    builder.Services.AddHostedService<TechLogWorker>();

    LogRoutingSummary(exporterOptions);

    var host = builder.Build();
    await host.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Критический сбой при запуске службы OneSLogExporter");
    try
    {
        File.AppendAllText(emergencyLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FATAL: {ex}\n");
    }
    catch { }
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static void LogRoutingSummary(ExporterOptions options)
{
    var evSource = options.EventLog.Enabled 
        ? $"ВКЛЮЧЕН [База: '{(string.IsNullOrWhiteSpace(options.EventLog.DatabaseName) ? "Все" : options.EventLog.DatabaseName)}', Путь: '{options.EventLog.DirectoryPath}']"
        : "ОТКЛЮЧЕН";
    var techSource = options.TechLog.Enabled 
        ? $"ВКЛЮЧЕН [Путь: '{options.TechLog.DirectoryPath}']"
        : "ОТКЛЮЧЕН";

    var fileDumpEv = options.FileDump.IsEventLogActive ? $"ВКЛЮЧЕН ({options.FileDump.EventLogDirectoryPath})" : "ОТКЛЮЧЕН";
    var fileDumpTech = options.FileDump.IsTechLogActive ? $"ВКЛЮЧЕН ({options.FileDump.TechLogDirectoryPath})" : "ОТКЛЮЧЕН";

    var chEv = options.ClickHouse.IsEventLogActive ? $"ВКЛЮЧЕН (таблица '{options.ClickHouse.EventLogTable}')" : "ОТКЛЮЧЕН (НЕ отправляется)";
    var chTech = options.ClickHouse.IsTechLogActive ? $"ВКЛЮЧЕН (таблица '{options.ClickHouse.TechLogTable}')" : "ОТКЛЮЧЕН (НЕ отправляется)";

    var elEv = options.Elastic.IsEventLogActive ? $"ВКЛЮЧЕН (префикс '{options.Elastic.EventLogIndexPrefix}')" : "ОТКЛЮЧЕН (НЕ отправляется)";
    var elTech = options.Elastic.IsTechLogActive ? $"ВКЛЮЧЕН (префикс '{options.Elastic.TechLogIndexPrefix}')" : "ОТКЛЮЧЕН (НЕ отправляется)";

    var kibanaEv = options.Kibana.IsEventLogActive ? "ВКЛЮЧЕН" : "ОТКЛЮЧЕН";
    var kibanaTech = options.Kibana.IsTechLogActive ? "ВКЛЮЧЕН" : "ОТКЛЮЧЕН";

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("\n================================================================================");
    sb.AppendLine(" OneSLogExporter — Матрица маршрутизации данных:");
    sb.AppendLine("--------------------------------------------------------------------------------");
    sb.AppendLine($" [Парсинг 1С (рубильники чтения)]: ");
    sb.AppendLine($"   - Журнал Регистрации (EventLog.Enabled): {evSource}");
    sb.AppendLine($"   - Технологический Журнал (TechLog.Enabled): {techSource}");
    sb.AppendLine("--------------------------------------------------------------------------------");
    sb.AppendLine(" [Сервисы экспорта (TechLogEnabled / EventLogEnabled)]: ");
    sb.AppendLine("   - Локальный JSON-дамп (FileDump):");
    sb.AppendLine($"       -> Журнал Регистрации: {fileDumpEv}");
    sb.AppendLine($"       -> Технический Журнал: {fileDumpTech}");
    sb.AppendLine($"   - ClickHouse ({options.ClickHouse.ServerUrl}, БД: {options.ClickHouse.Database}):");
    sb.AppendLine($"       -> Журнал Регистрации: {chEv}");
    sb.AppendLine($"       -> Технический Журнал: {chTech}");
    sb.AppendLine($"   - Elasticsearch ({options.Elastic.ServerUrl}):");
    sb.AppendLine($"       -> Журнал Регистрации: {elEv}");
    sb.AppendLine($"       -> Технический Журнал: {elTech}");
    sb.AppendLine($"   - Kibana ({options.Kibana.ServerUrl}):");
    sb.AppendLine($"       -> Журнал Регистрации: {kibanaEv}");
    sb.AppendLine($"       -> Технический Журнал: {kibanaTech}");
    sb.AppendLine("================================================================================");

    var text = sb.ToString();
    Console.WriteLine(text);
    Log.Information("{Summary}", text);
}
