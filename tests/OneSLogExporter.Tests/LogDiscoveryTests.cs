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
}
