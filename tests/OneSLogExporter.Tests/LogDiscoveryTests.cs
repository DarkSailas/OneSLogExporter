using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Services;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class LogDiscoveryTests : IDisposable
{
    private readonly string _testRoot;

    public LogDiscoveryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "LogDiscTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch { }
    }

    [Fact]
    public void FindTechLogFiles_WithSingleFile_ShouldDiscoverDirectly()
    {
        var singleFile = Path.Combine(_testRoot, "26083114.log");
        File.WriteAllText(singleFile, "test");

        var results = LogDiscovery.FindTechLogFiles(singleFile).ToList();

        results.Should().HaveCount(1);
        results[0].FilePath.Should().Be(singleFile);
    }

    [Fact]
    public void FindTechLogFiles_WithArbitraryFileName_ShouldDiscoverDirectly()
    {
        var singleFile = Path.Combine(_testRoot, "my_custom_techlog.txt");
        File.WriteAllText(singleFile, "test");

        var results = LogDiscovery.FindTechLogFiles(singleFile).ToList();

        results.Should().HaveCount(1);
        results[0].FilePath.Should().Be(singleFile);
    }

    [Fact]
    public void ParseProcessInfo_WithProcessInFileName_ShouldExtractProcessAndPid()
    {
        var file = Path.Combine(_testRoot, "rphost_5678_26083114.log");

        var (proc, pid) = LogDiscovery.ParseProcessInfo(file);

        proc.Should().Be("rphost");
        pid.Should().Be("5678");
    }

    [Fact]
    public void FindEventLogFiles_WithSingleLgpFile_ShouldDiscoverDirectly()
    {
        var singleLgp = Path.Combine(_testRoot, "20260831000000.lgp");
        File.WriteAllText(singleLgp, "test");

        var results = LogDiscovery.FindEventLogFiles(singleLgp).ToList();

        results.Should().HaveCount(1);
        results[0].Should().Be(singleLgp);
    }

    [Fact]
    public void FindEventLogDictionary_ShouldFindInParentFolder()
    {
        var lgfFile = Path.Combine(_testRoot, "1Cv8.lgf");
        File.WriteAllText(lgfFile, "{1,}");

        var subDir = Path.Combine(_testRoot, "sub1", "sub2");
        Directory.CreateDirectory(subDir);
        var lgpFile = Path.Combine(subDir, "20260831000000.lgp");
        File.WriteAllText(lgpFile, "test");

        var foundDict = LogDiscovery.FindEventLogDictionary(lgpFile);

        foundDict.Should().NotBeNull();
        foundDict.Should().Be(lgfFile);
    }

    [Fact]
    public void ParseEntry_WithoutDictionary_ShouldFallbackGracefully()
    {
        var sampleLgpEntry = @"{20260817123045,N,
{0,0},1,1,1,1,
I,
""Успешный вход в систему""""1С:Предприятие"""" с клиента"",
1,
""ДанныеСеанса"",
1,
1,
1,
1,
1,
""12345""
},";

        var emptyDict = new LgfDictionary();
        var doc = EventLogParser.ParseEntry(sampleLgpEntry, emptyDict);

        doc.Should().NotBeNull();
        doc!.User.Should().Be("User #1");
        doc.App.Should().Be("App #1");
        doc.Importance.Should().Be("Информация");
        doc.Comment.Should().Be("Успешный вход в систему\"1С:Предприятие\" с клиента");
        doc.Session.Should().Be("12345");
        doc.DateFormatted.Should().Be("2026-08-17 12:30:45");
    }

    [Fact]
    public void TechLogParser_ExtractDateTime_ShouldHandlePrefixedFilenames()
    {
        var file = Path.Combine(_testRoot, "rphost_1234_26083114.log");

        var (year, month, day, hour) = TechLogParser.ExtractDateTime(file);

        year.Should().Be(2026);
        month.Should().Be(8);
        day.Should().Be(31);
        hour.Should().Be(14);
    }

    [Fact]
    public void FilterEventLogFilesByDate_ShouldSelectOnlyOverlappingPartitions()
    {
        var files = new[]
        {
            @"C:\1Cv8Log\20260810000000.lgp",
            @"C:\1Cv8Log\20260817000000.lgp",
            @"C:\1Cv8Log\20260824000000.lgp",
            @"C:\1Cv8Log\20260831000000.lgp",
        };

        // Пользователь выбрал период 03.09 - 04.09
        var from = new DateTime(2026, 9, 3);
        var to = new DateTime(2026, 9, 4);

        var filtered = LogDiscovery.FilterEventLogFilesByDate(files, from, to);

        filtered.Should().HaveCount(1);
        filtered[0].Should().Be(@"C:\1Cv8Log\20260831000000.lgp");
    }

    [Fact]
    public void FilterEventLogFilesByDate_ShouldSelectMultipleSpanningPartitions()
    {
        var files = new[]
        {
            @"C:\1Cv8Log\20260810000000.lgp",
            @"C:\1Cv8Log\20260817000000.lgp",
            @"C:\1Cv8Log\20260824000000.lgp",
            @"C:\1Cv8Log\20260831000000.lgp",
        };

        // Пользователь выбрал период 20.08 - 25.08
        var from = new DateTime(2026, 8, 20);
        var to = new DateTime(2026, 8, 25);

        var filtered = LogDiscovery.FilterEventLogFilesByDate(files, from, to);

        filtered.Should().HaveCount(2);
        filtered.Should().Contain(@"C:\1Cv8Log\20260817000000.lgp");
        filtered.Should().Contain(@"C:\1Cv8Log\20260824000000.lgp");
    }

    [Fact]
    public void DiscoverInfobases_ShouldParseClusterRegistryAndFindInfobaseFolders()
    {
        var guid1 = "33333333-aaaa-bbbb-cccc-111111111111";
        var guid2 = "44444444-aaaa-bbbb-cccc-222222222222";

        var clstContent = $$"""
        {1,
        {2,
        {"{{guid1}}","TestBase1","","","","","","","","","",""},
        {"{{guid2}}","Accounting_Corp","","","","","","","","","",""}
        }
        }
        """;
        File.WriteAllText(Path.Combine(_testRoot, "1CV8Clst.lst"), clstContent);

        var dir1 = Path.Combine(_testRoot, guid1, "1Cv8Log");
        Directory.CreateDirectory(dir1);
        File.WriteAllText(Path.Combine(dir1, "1Cv8.lgf"), "{1,}");

        var dir2 = Path.Combine(_testRoot, guid2, "1Cv8Log");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(dir2, "1Cv8.lgf"), "{1,}");

        var discovered = LogDiscovery.DiscoverInfobases(_testRoot);

        discovered.Should().HaveCount(2);
        discovered.Should().Contain(b => b.Name == "TestBase1" && b.Guid == guid1);
        discovered.Should().Contain(b => b.Name == "Accounting_Corp" && b.Guid == guid2);

        var resolved = LogDiscovery.ResolveInfobases(_testRoot, "Accounting_Corp");
        resolved.Should().HaveCount(1);
        resolved[0].Name.Should().Be("Accounting_Corp");
        resolved[0].Guid.Should().Be(guid2);
        resolved[0].DirectoryPath.Should().Be(dir2);
        resolved[0].DictionaryPath.Should().Be(Path.Combine(dir2, "1Cv8.lgf"));
    }

    private string CreateInfobaseLogDir(string guid, params string[] lgpNames)
    {
        var dir = Path.Combine(_testRoot, guid, "1Cv8Log");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "1Cv8.lgf"), "dict-" + guid);
        foreach (var name in lgpNames)
            File.WriteAllText(Path.Combine(dir, name), "data");
        return dir;
    }

    [Fact]
    public void ScanEventLogFiles_ClusterRoot_ShouldAssignEachFileItsOwnDictionary()
    {
        var dir1 = CreateInfobaseLogDir("base-one", "20260901000000.lgp", "20260902000000.lgp");
        var dir2 = CreateInfobaseLogDir("base-two", "20260815000000.lgp");
        File.WriteAllText(Path.Combine(dir1, "20260901000000.lgx"), "index");

        var entries = LogDiscovery.ScanEventLogFiles(_testRoot);

        entries.Should().HaveCount(3);
        entries.Where(e => e.FilePath.StartsWith(dir1, StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(e => e.DictionaryPath == Path.Combine(dir1, "1Cv8.lgf"));
        entries.Single(e => e.FilePath.StartsWith(dir2, StringComparison.OrdinalIgnoreCase))
            .DictionaryPath.Should().Be(Path.Combine(dir2, "1Cv8.lgf"));
        entries.Should().OnlyContain(e => e.Length == 4);
    }

    [Fact]
    public void ScanEventLogFiles_DictionaryInParentFolder_ShouldBeUsedForNestedFiles()
    {
        var logDir = Path.Combine(_testRoot, "1Cv8Log");
        var nested = Path.Combine(logDir, "archive");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(logDir, "1Cv8.lgf"), "dict");
        File.WriteAllText(Path.Combine(nested, "20260101000000.lgp"), "data");

        var entries = LogDiscovery.ScanEventLogFiles(_testRoot);

        entries.Should().ContainSingle()
            .Which.DictionaryPath.Should().Be(Path.Combine(logDir, "1Cv8.lgf"));
    }

    [Fact]
    public void ScanEventLogFiles_SingleFileInputs_ShouldFollowDiscoveryRules()
    {
        var dir = CreateInfobaseLogDir("base-one", "20260901000000.lgp");
        var lgp = Path.Combine(dir, "20260901000000.lgp");
        var lgx = Path.Combine(dir, "20260901000000.lgx");
        var json = Path.Combine(_testRoot, "dump.json");
        File.WriteAllText(lgx, "index");
        File.WriteAllText(json, "[]");

        LogDiscovery.ScanEventLogFiles(lgp).Should().ContainSingle()
            .Which.Should().Match<EventLogFileEntry>(e => e.FilePath == lgp && e.DictionaryPath == Path.Combine(dir, "1Cv8.lgf"));
        LogDiscovery.ScanEventLogFiles(lgx).Should().ContainSingle().Which.FilePath.Should().Be(lgp);
        LogDiscovery.ScanEventLogFiles(Path.Combine(dir, "1Cv8.lgf")).Should().ContainSingle().Which.FilePath.Should().Be(lgp);
        LogDiscovery.ScanEventLogFiles(json).Should().ContainSingle()
            .Which.Should().Match<EventLogFileEntry>(e => e.FilePath == json && e.DictionaryPath == null);
        LogDiscovery.ScanEventLogFiles(Path.Combine(_testRoot, "missing")).Should().BeEmpty();
    }

    [Fact]
    public void ScanEventLogFiles_ShouldReportProgressAndHonorCancellation()
    {
        CreateInfobaseLogDir("base-one", "20260901000000.lgp");
        var reports = new List<(int Directories, int Files)>();
        var progress = new SyncProgress(reports);

        LogDiscovery.ScanEventLogFiles(_testRoot, progress);

        reports.Should().NotBeEmpty();
        reports[^1].Files.Should().Be(1);
        reports[^1].Directories.Should().Be(3);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => LogDiscovery.ScanEventLogFiles(_testRoot, null, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ScanEventLogFiles_ClusterRoot_ShouldNotWalkServiceDirectories()
    {
        var logDir = CreateInfobaseLogDir("base-one", "20260901000000.lgp");
        var baseDir = Path.GetDirectoryName(logDir)!;
        // Полнотекстовый индекс и данные сеансов: тысячи файлов, журнала там нет
        Directory.CreateDirectory(Path.Combine(baseDir, "1Cv8FTxt", "part1", "part2"));
        Directory.CreateDirectory(Path.Combine(_testRoot, "snccntx-one", "data"));
        Directory.CreateDirectory(Path.Combine(_testRoot, "base-empty", "1Cv8FTxt", "part1"));
        var reports = new List<(int Directories, int Files)>();

        var entries = LogDiscovery.ScanEventLogFiles(_testRoot, new SyncProgress(reports));

        entries.Should().ContainSingle().Which.FilePath.Should().Be(Path.Combine(logDir, "20260901000000.lgp"));
        // Корень, base-one, его 1Cv8Log и base-empty
        reports[^1].Directories.Should().Be(4);
    }

    [Fact]
    public void ScanEventLogFiles_ManyBases_ShouldReturnAllFilesInStableOrder()
    {
        var expected = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            var dir = CreateInfobaseLogDir($"base-{i:D2}", "20260901000000.lgp", "20260902000000.lgp");
            expected.Add(Path.Combine(dir, "20260901000000.lgp"));
            expected.Add(Path.Combine(dir, "20260902000000.lgp"));
        }

        var entries = LogDiscovery.ScanEventLogFiles(_testRoot);

        entries.Select(e => e.FilePath).Should().Equal(expected);
        entries.Should().OnlyContain(e => e.DictionaryPath == Path.Combine(Path.GetDirectoryName(e.FilePath)!, "1Cv8.lgf"));
    }

    private sealed class SyncProgress(List<(int Directories, int Files)> reports) : IProgress<(int Directories, int Files)>
    {
        public void Report((int Directories, int Files) value) => reports.Add(value);
    }

    private static EventLogFileEntry Entry(string dir, string name, DateTime? lastWrite = null)
        => new(Path.Combine(dir, name), 10, lastWrite ?? new DateTime(2026, 1, 1), null);

    [Fact]
    public void SelectEventLogFilesByDate_LatestOnly_ShouldKeepLatestDayOfEveryDirectory()
    {
        var dirA = Path.Combine(_testRoot, "a");
        var dirB = Path.Combine(_testRoot, "b");
        var files = new List<EventLogFileEntry>
        {
            Entry(dirA, "20260901000000.lgp"),
            Entry(dirA, "20260905000000.lgp"),
            Entry(dirB, "20260710000000.lgp"),
            Entry(dirB, "20260720000000.lgp"),
            Entry(dirB, "20260720120000.lgp")
        };

        var selected = LogDiscovery.SelectEventLogFilesByDate(files, null, null, latestOnly: true);

        selected.Select(e => Path.GetFileName(e.FilePath)).Should().BeEquivalentTo(
            "20260905000000.lgp", "20260720000000.lgp", "20260720120000.lgp");
    }

    [Fact]
    public void SelectEventLogFilesByDate_WithInterval_ShouldFilterEachDirectorySeparately()
    {
        var dirA = Path.Combine(_testRoot, "a");
        var dirB = Path.Combine(_testRoot, "b");
        var files = new List<EventLogFileEntry>
        {
            // База A: файл от 1 сентября покрывает интервал до 20 сентября
            Entry(dirA, "20260901000000.lgp"),
            Entry(dirA, "20260920000000.lgp"),
            // База B: её файлы не должны обрезать интервал файлов базы A
            Entry(dirB, "20260902000000.lgp"),
            Entry(dirB, "20260903000000.lgp")
        };

        var day = new DateTime(2026, 9, 10);
        var selected = LogDiscovery.SelectEventLogFilesByDate(files, day, day, latestOnly: false);

        selected.Select(e => e.FilePath).Should().BeEquivalentTo(
            [Path.Combine(dirA, "20260901000000.lgp"), Path.Combine(dirB, "20260903000000.lgp")]);
    }

    [Fact]
    public void SelectEventLogFilesByDate_WithoutFilters_ShouldReturnAllNewestFirst()
    {
        var dirA = Path.Combine(_testRoot, "a");
        var files = new List<EventLogFileEntry>
        {
            Entry(dirA, "20260901000000.lgp"),
            Entry(dirA, "20260905000000.lgp"),
            Entry(dirA, "custom.lgp", new DateTime(2026, 9, 3, 15, 0, 0))
        };

        var selected = LogDiscovery.SelectEventLogFilesByDate(files, null, null, latestOnly: false);

        selected.Select(e => Path.GetFileName(e.FilePath)).Should().Equal(
            "20260905000000.lgp", "custom.lgp", "20260901000000.lgp");
    }

    [Fact]
    public void SelectEventLogFilesByDate_Interval_ShouldAlwaysKeepFilesWithoutDateInName()
    {
        var dirA = Path.Combine(_testRoot, "a");
        var dirB = Path.Combine(_testRoot, "b");
        var today = new DateTime(2026, 10, 6);
        var files = new List<EventLogFileEntry>
        {
            Entry(dirA, "1Cv8.lgd", today),
            Entry(dirB, "1Cv8.lgd", today)
        };

        // Файл .lgd хранит всю историю, дата его изменения ничего не говорит о периоде записей
        var selected = LogDiscovery.SelectEventLogFilesByDate(
            files, new DateTime(2026, 9, 1), new DateTime(2026, 9, 2), latestOnly: false);

        selected.Should().HaveCount(2);
    }

    [Fact]
    public void ScanEventLogFiles_OnlyOneBaseHasDictionary_ShouldNotGiveItToOtherBases()
    {
        var dir1 = CreateInfobaseLogDir("base-one", "20260901000000.lgp");
        var dir2 = CreateInfobaseLogDir("base-two", "20260815000000.lgp");
        File.Delete(Path.Combine(dir2, "1Cv8.lgf"));

        var entries = LogDiscovery.ScanEventLogFiles(_testRoot);

        entries.Single(e => e.FilePath.StartsWith(dir1, StringComparison.OrdinalIgnoreCase))
            .DictionaryPath.Should().Be(Path.Combine(dir1, "1Cv8.lgf"));
        entries.Single(e => e.FilePath.StartsWith(dir2, StringComparison.OrdinalIgnoreCase))
            .DictionaryPath.Should().BeNull();
    }
}
