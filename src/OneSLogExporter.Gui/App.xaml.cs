using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using OneSLogExporter.Core.Services;
using Serilog;

namespace OneSLogExporter.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Очистка старых/зависших сессионных кэшей при запуске
            SessionCacheService.CleanupAllOrphanedTempFiles();

            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var builder = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory);

            if (File.Exists(configPath))
            {
                OneSLogExporter.Core.Serialization.ConfigSanitizer.SanitizeConfigFile(configPath);
                builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            }

            var configuration = builder.Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .WriteTo.File(
                    path: Path.Combine(AppContext.BaseDirectory, "logs", "ones_gui_.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10485760,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Log.Information("Графическое приложение OneSLogExporter GUI успешно запущено.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ошибка инициализации логгера GUI: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Удаление всех временных файлов при закрытии программы
            SessionCacheService.CleanupAllOrphanedTempFiles();
        }
        catch { }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
