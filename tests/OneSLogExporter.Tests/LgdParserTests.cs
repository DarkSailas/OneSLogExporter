using FluentAssertions;
using Microsoft.Data.Sqlite;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class LgdParserTests : IDisposable
{
    private readonly string _tempDbPath;

    public LgdParserTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), "1Cv8Test_" + Guid.NewGuid().ToString("N") + ".lgd");
        CreateSampleLgdDatabase(_tempDbPath);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath))
                File.Delete(_tempDbPath);
        }
        catch { }
    }

    private static void CreateSampleLgdDatabase(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE UserCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO UserCodes VALUES (1, 'Администратор');

CREATE TABLE AppCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO AppCodes VALUES (1, '1CV8C');

CREATE TABLE EventCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO EventCodes VALUES (1, '_$Session$_.Start');

CREATE TABLE MetadataCodes (code INTEGER PRIMARY KEY, name TEXT);
INSERT INTO MetadataCodes VALUES (1, 'Справочник.Номенклатура');

CREATE TABLE EventLog (
    rowID INTEGER PRIMARY KEY,
    severity INTEGER,
    date INTEGER,
    connectID INTEGER,
    session INTEGER,
    transactionStatus INTEGER,
    transactionID INTEGER,
    userCode INTEGER,
    appCode INTEGER,
    eventCode INTEGER,
    comment TEXT,
    dataPresentation TEXT,
    metadataCodes TEXT
);

-- 2026-08-17 12:30:45 UTC = 639230562450000000 ticks in .NET. Divided by 1000 = 639230562450000
INSERT INTO EventLog VALUES (
    1,
    0,
    639230562450000,
    48187,
    12345,
    1,
    999,
    1,
    1,
    1,
    'Вход в систему',
    'ДанныеСеанса',
    '1'
);

INSERT INTO EventLog VALUES (
    2,
    2,
    639230562450000,
    48188,
    12345,
    2,
    1000,
    1,
    1,
    1,
    'Критическая ошибка базы данных',
    'ОбъектНеНайден',
    '1'
);
";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task ParseLgdAsync_ShouldReadAndDecodeAllRecords()
    {
        var docs = new List<EventLogDoc>();
        await foreach (var doc in LgdParser.ParseLgdAsync(_tempDbPath))
        {
            docs.Add(doc);
        }

        docs.Should().HaveCount(2);

        // Сортировка по убыванию (сначала новые)
        var errDoc = docs[0];
        errDoc.Importance.Should().Be("Ошибка");
        errDoc.Comment.Should().Be("Критическая ошибка базы данных");
        errDoc.User.Should().Be("Администратор");
        errDoc.App.Should().Be("1CV8C");
        errDoc.Event.Should().Be("Сеанс. Начало");
        errDoc.Tran.Should().Be("R(1000)");

        var infoDoc = docs[1];
        infoDoc.Importance.Should().Be("Информация");
        infoDoc.Comment.Should().Be("Вход в систему");
        infoDoc.Tran.Should().Be("X(999)");
    }

    [Fact]
    public async Task GetMaxRowIdAsync_ShouldReturnHighestRowId()
    {
        var maxRowId = await LgdParser.GetMaxRowIdAsync(_tempDbPath);
        maxRowId.Should().Be(2);
    }

    [Fact]
    public async Task ParseLgdIncrementalAsync_ShouldReturnNewerRecordsInAscendingOrder()
    {
        // 1. Читаем с rowId 0 -> получаем 2 записи
        var (allDocs, newMax1) = await LgdParser.ParseLgdIncrementalAsync(_tempDbPath, 0);
        allDocs.Should().HaveCount(2);
        newMax1.Should().Be(2);
        allDocs[0].Id.Should().Contain("_1_");
        allDocs[1].Id.Should().Contain("_2_");

        // 2. Читаем с rowId 1 -> получаем только 2-ю запись
        var (partialDocs, newMax2) = await LgdParser.ParseLgdIncrementalAsync(_tempDbPath, 1);
        partialDocs.Should().HaveCount(1);
        newMax2.Should().Be(2);
        partialDocs[0].Id.Should().Contain("_2_");

        // 3. Читаем с rowId 2 -> записей нет
        var (emptyDocs, newMax3) = await LgdParser.ParseLgdIncrementalAsync(_tempDbPath, 2);
        emptyDocs.Should().BeEmpty();
        newMax3.Should().Be(2);
    }

    [Fact]
    public void IndexNamingHelper_ShouldFormatSeparationCorrectly()
    {
        var date = new DateTime(2026, 9, 11, 14, 30, 0, DateTimeKind.Utc);

        IndexNamingHelper.BuildIndexName("events", "prod", "Day", date).Should().Be("events_prod_20260911");
        IndexNamingHelper.BuildIndexName("events", "prod", "D", date).Should().Be("events_prod_20260911");
        IndexNamingHelper.BuildIndexName("events", "prod", "Month", date).Should().Be("events_prod_202609");
        IndexNamingHelper.BuildIndexName("events", "prod", "M", date).Should().Be("events_prod_202609");
        IndexNamingHelper.BuildIndexName("events", "prod", "Hour", date).Should().Be("events_prod_2026091114");
        IndexNamingHelper.BuildIndexName("events", "prod", "H", date).Should().Be("events_prod_2026091114");
        IndexNamingHelper.BuildIndexName("events", "prod", "None", date).Should().Be("events_prod_all");
        IndexNamingHelper.BuildIndexName("events", "prod", "all", date).Should().Be("events_prod_all");
    }
}
