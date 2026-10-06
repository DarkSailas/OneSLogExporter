using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Services;
using Xunit;

namespace OneSLogExporter.Tests;

/// <summary>
/// Автономные юнит-тесты парсинга Технологического Журнала 1С.
/// </summary>
public sealed class TechLogParserTests
{
    private const string SampleLine = "23:48.384002-31985,DBMSSQL,5,p:processName=DemoDb,t:clientID=27,t:applicationName=WebServerExtension,t:connectID=48187,Usr=Администратор,Context='HTTPСервис.API.Модуль : 125'";

    [Fact]
    public void ParseBlock_ShouldExtractCorrectFields()
    {
        var doc = TechLogParser.ParseBlock(SampleLine, 2026, 7, 30, 8, "rphost", "9000");

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("DBMSSQL");
        doc.Level.Should().Be(5);
        doc.DateFormatted.Should().Be("2026-07-30 08:23:48.384");
        doc.Duration.Should().Be(31985);
        doc.DurationMs.Should().Be(31.985);
        doc.DurationSec.Should().Be(0.031985);
        doc.DurationFormatted.Should().Be("31.98 ms");
        doc.ProcessName.Should().Be("rphost");
        doc.ProcessId.Should().Be("9000");
        doc.User.Should().Be("Администратор");
        doc.ClientId.Should().Be("27");
        doc.ConnectId.Should().Be("48187");
        doc.App.Should().Be("WebServerExtension");
        doc.Context.Should().Be("HTTPСервис.API.Модуль : 125");
    }

    [Fact]
    public void SanitizeMultilineText_ShouldPreserveNewlinesAndConvertTabsToSpaces()
    {
        var rawContext = "ОбщийМодуль.Рассылка.Модуль : 10\r\n\tОбщийМодуль.Отчеты.Модуль : 20\r\n\t\tВыполнить()";
        var sanitized = TechLogParser.SanitizeMultilineText(rawContext);

        sanitized.Should().Contain("\n", "переносы строк должны сохраняться для читаемого стека вызовов");
        sanitized.Should().NotContain("\r", "возвраты каретки должны удаляться");
        sanitized.Should().NotContain("\t", "табуляция должна заменяться пробелами для форматирования в UI");
        sanitized.Should().Be("ОбщийМодуль.Рассылка.Модуль : 10\n  ОбщийМодуль.Отчеты.Модуль : 20\n    Выполнить()");
    }

    [Fact]
    public async Task ParseFileAsync_WithTempFile_ShouldStreamDocuments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TechLogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "26073008.log");

        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLine + "\n" + SampleLine + "\n");

            var docs = new List<TechLogDoc>();
            await foreach (var doc in TechLogParser.ParseFileAsync(tempFile, "rphost", "9000"))
            {
                docs.Add(doc);
            }

            docs.Should().HaveCount(2);
            docs[0].Event.Should().Be("DBMSSQL");
            docs[0].ProcessName.Should().Be("rphost");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ParseFileFromOffsetAsync_WithIncrementalOffset_ShouldReturnOnlyNewDocuments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TechLogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "26073008.log");

        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLine + "\n");

            // 1. Первая итерация: считываем со смещения 0
            var (firstBatch, midPos) = await TechLogParser.ParseFileFromOffsetAsync(tempFile, "rphost", "9000", 0);
            firstBatch.Should().NotBeEmpty();
            midPos.Should().BeGreaterThan(0);

            // 2. Вторая итерация со смещения midPos (конец файла) не должна вернуть дубликатов
            var (secondBatch, finalPos) = await TechLogParser.ParseFileFromOffsetAsync(tempFile, "rphost", "9000", midPos);
            secondBatch.Should().BeEmpty();
            finalPos.Should().Be(midPos);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SanitizeText_WithOversizedField_ShouldTruncateSafely()
    {
        var hugeString = new string('A', 1500);
        var result = TechLogParser.SanitizeText(hugeString, maxLength: 1000);

        result.Should().StartWith(new string('A', 1000));
        result.Should().Contain("[TRUNCATED: 1500 -> 1000 chars]");
    }

    [Fact]
    public void ParseBlock_WithLongDurationInfo_ShouldExtractActiveStatusAndFields()
    {
        const string longLine = "45:07.345000-10000000,LONGDURATIONINFO,4,process=rphost,p:processName=Base,OSThread=1234,LongInfoName=DBMSSQL,LongInfoWait=10000000,Context='Справочник.Номенклатура.МодульОбъекта : 45'";
        var doc = TechLogParser.ParseBlock(longLine, 2026, 8, 18, 10, "rphost", "1234");

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("LONGDURATIONINFO");
        doc.IsActiveOperation.Should().BeTrue();
        doc.ExecutionStatus.Should().Be("Выполняется");
        doc.OSThread.Should().Be("1234");
        doc.LongInfoName.Should().Be("DBMSSQL");
        doc.LongInfoWait.Should().Be(10000000);
    }

    [Fact]
    public void CompactSerialization_ShouldPreserveCyrillicWithoutUnicodeEscapes()
    {
        var doc = TechLogParser.ParseBlock(SampleLine, 2026, 7, 30, 8, "rphost", "9000");
        doc.Should().NotBeNull();

        var json = System.Text.Json.JsonSerializer.Serialize(doc, OneSLogExporter.Core.Serialization.LogJsonContext.Compact.TechLogDoc);

        json.Should().Contain("\"User\":\"Администратор\"");
        json.Should().Contain("\"Context\":\"HTTPСервис.API.Модуль : 125\"");
        json.Should().NotContain(@"\u04");
    }

    [Fact]
    public async Task FileDumper_ShouldRotateRingSlotsAndKeepCyrillicReadable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DumperTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settings = new FileDumpSettings
            {
                TechLogEnabled = true,
                TechLogDirectoryPath = tempDir,
                TechLogFileNamePattern = "data_tglog_{N}.json",
                RetainedFileCountLimit = 5,
                MaxFileRecordCount = 1
            };
            var dumper = new FileDumper(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileDumper>.Instance);

            var doc = TechLogParser.ParseBlock(SampleLine, 2026, 7, 30, 8, "rphost", "9000")!;

            // Итерация 1: пишем 1 запись -> слот 1
            await dumper.DumpTechLogsAsync("test_prefix", [doc]);

            var slot1 = Path.Combine(tempDir, "data_tglog_1.json");
            File.Exists(slot1).Should().BeTrue();
            var lines1 = await File.ReadAllLinesAsync(slot1);
            lines1.Should().HaveCount(1);
            lines1[0].Should().Contain("\"User\":\"Администратор\"");
            lines1[0].Should().NotContain(@"\u04");

            // Итерация 2: следующая пачка пишется в слот 2
            await dumper.DumpTechLogsAsync("test_prefix", [doc]);

            var slot2 = Path.Combine(tempDir, "data_tglog_2.json");
            File.Exists(slot2).Should().BeTrue();
            var lines2 = await File.ReadAllLinesAsync(slot2);
            lines2.Should().HaveCount(1);
            lines2[0].Should().Contain("\"User\":\"Администратор\"");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FileDumper_ShouldOverwriteOldestSlotWhenLimitReached()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DumperRingTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settings = new FileDumpSettings
            {
                TechLogEnabled = true,
                TechLogDirectoryPath = tempDir,
                TechLogFileNamePattern = "data_tglog_{N}.json",
                RetainedFileCountLimit = 2,
                MaxFileRecordCount = 1
            };
            var dumper = new FileDumper(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileDumper>.Instance);
            var doc = TechLogParser.ParseBlock(SampleLine, 2026, 7, 30, 8, "rphost", "9000")!;

            // Заполняем слоты 1 и 2
            await dumper.DumpTechLogsAsync("test", [doc]);
            await dumper.DumpTechLogsAsync("test", [doc]);

            var slot1 = Path.Combine(tempDir, "data_tglog_1.json");
            var slot2 = Path.Combine(tempDir, "data_tglog_2.json");
            File.Exists(slot1).Should().BeTrue();
            File.Exists(slot2).Should().BeTrue();

            // Искусственно состарим слот 1
            File.SetLastWriteTimeUtc(slot1, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(slot2, DateTime.UtcNow.AddMinutes(-5));

            // Третья запись при лимите 2 должна перезаписать самый старый слот (слот 1)
            await dumper.DumpTechLogsAsync("test", [doc]);

            var info1 = new FileInfo(slot1);
            info1.LastWriteTimeUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FileDumper_ShouldDumpTechLogAndEventLogInSeparateDirectories()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "DumperSepTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);

        try
        {
            var settings = new FileDumpSettings
            {
                TechLogEnabled = true,
                EventLogEnabled = true,
                DirectoryPath = baseDir,
                RetainedFileCountLimit = 30,
                MaxTotalSizeMb = 500
            };
            var dumper = new FileDumper(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileDumper>.Instance);
            var tgDoc = TechLogParser.ParseBlock(SampleLine, 2026, 7, 30, 8, "rphost", "9000")!;
            var evDoc = new EventLogDoc
            {
                Id = "ev_1",
                Date = DateTime.UtcNow,
                DateFormatted = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                Event = "_$Session$_.Start",
                User = "Администратор"
            };

            await dumper.DumpTechLogsAsync("test_tg", [tgDoc]);
            await dumper.DumpEventLogsAsync("test_ev", [evDoc]);

            var tgFile = Path.Combine(baseDir, "techlog", "data_tglog_1.json");
            var evFile = Path.Combine(baseDir, "eventlog", "data_evlog_1.json");

            File.Exists(tgFile).Should().BeTrue("файлы ТЖ должны сохраняться в подкаталог techlog");
            File.Exists(evFile).Should().BeTrue("файлы ЖР должны сохраняться в подкаталог eventlog");
        }
        finally
        {
            if (Directory.Exists(baseDir))
                Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task FileDumper_ShouldEnforceMaxTotalSizeMbLimitAcrossDumps()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DumperSizeLimitTest_" + Guid.NewGuid().ToString("N"));
        var tgDir = Path.Combine(tempDir, "techlog");
        Directory.CreateDirectory(tgDir);

        try
        {
            var settings = new FileDumpSettings
            {
                TechLogEnabled = true,
                DirectoryPath = tempDir,
                TechLogDirectoryPath = tgDir,
                RetainedFileCountLimit = 10,
                MaxTotalSizeMb = 1 // Лимит 1 МБ в сумме
            };
            var dumper = new FileDumper(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<FileDumper>.Instance);

            // Создадим предварительно 2 файла по 600 КБ каждый (в сумме 1.2 МБ > 1 МБ)
            var file1 = Path.Combine(tgDir, "data_tglog_1.json");
            var file2 = Path.Combine(tgDir, "data_tglog_2.json");
            await File.WriteAllBytesAsync(file1, new byte[600 * 1024]);
            await File.WriteAllBytesAsync(file2, new byte[600 * 1024]);
            File.SetLastWriteTimeUtc(file1, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(file2, DateTime.UtcNow.AddMinutes(-5));

            var tgDoc = TechLogParser.ParseBlock(SampleLine, 2026, 7, 30, 8, "rphost", "9000")!;
            // Новая запись должна инициировать очистку старых файлов для соблюдения суммарного лимита 1 МБ
            await dumper.DumpTechLogsAsync("test", [tgDoc]);

            // Самый старый файл file1 должен быть удален для освобождения места
            File.Exists(file1).Should().BeFalse("старейший файл должен быть удален при превышении суммарного лимита 1 МБ");
            var remainingFiles = Directory.GetFiles(tgDir, "*.json");
            var totalBytes = remainingFiles.Sum(f => new FileInfo(f).Length);
            totalBytes.Should().BeLessThanOrEqualTo(1024 * 1024 + 1024); // Сумма в рамках лимита
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseBlock_WithFilterEmptyEvents_ShouldFilterEmptyEvents()
    {
        var emptyBlock = "00:00.000000-0,VCLIENT,0,process=1cv8";
        var meaningfulBlock = "00:00.000000-15000,DBMSSQL,0,process=1cv8,Sql='SELECT 1'";
        var errorBlock = "00:00.000000-0,EXCP,1,process=1cv8,descr='Error occurred'";

        // When filter is enabled (true)
        TechLogParser.ParseBlock(emptyBlock, 2026, 9, 24, 10, "1cv8", "1234", filterEmptyEvents: true).Should().BeNull();
        TechLogParser.ParseBlock(meaningfulBlock, 2026, 9, 24, 10, "1cv8", "1234", filterEmptyEvents: true).Should().NotBeNull();
        TechLogParser.ParseBlock(errorBlock, 2026, 9, 24, 10, "1cv8", "1234", filterEmptyEvents: true).Should().NotBeNull();

        // When filter is disabled (false)
        TechLogParser.ParseBlock(emptyBlock, 2026, 9, 24, 10, "1cv8", "1234", filterEmptyEvents: false).Should().NotBeNull();
    }
}
