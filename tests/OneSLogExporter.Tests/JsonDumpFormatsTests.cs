using System.Text;
using System.Text.Json;
using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Serialization;
using OneSLogExporter.Core.Services;
using Xunit;

namespace OneSLogExporter.Tests;

/// <summary>
/// Чтение JSON-дампов ЖР и ТЖ в любом виде: построчный JSON, массив, форматированный текст, объект-обёртка.
/// </summary>
public sealed class JsonDumpFormatsTests : IDisposable
{
    private readonly string _tempDir;

    public JsonDumpFormatsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"json_formats_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    private static EventLogDoc Ev(string id, string user = "Admin") => new()
    {
        Id = id,
        Date = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
        DateFormatted = "2026-09-13 10:00:00",
        Event = "_$Session$.Start",
        User = user,
        Comment = "Скобки { [ \" ] } внутри строки"
    };

    private static TechLogDoc Tg(string id, string sql = "SELECT 1") => new()
    {
        Id = id,
        Date = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
        DateFormatted = "2026-09-13 10:00:00.000",
        Duration = 500,
        DurationMs = 0.5,
        DurationSec = 0.0005,
        DurationFormatted = "0.50 ms",
        Event = "DBMSSQL",
        Level = 0,
        ProcessName = "rphost",
        ProcessId = "1234",
        Sql = sql,
        Properties = new Dictionary<string, string> { ["Usr"] = "Admin", ["Raw"] = "{a}[b]" }
    };

    private string PathOf(string name) => Path.Combine(_tempDir, name);

    private static async Task<List<EventLogDoc>> ReadEvAsync(string path)
    {
        List<EventLogDoc> docs = [];
        await foreach (var doc in EventLogParser.ParseJsonDumpAsync(path))
        {
            docs.Add(doc);
        }
        return docs;
    }

    private static async Task<List<TechLogDoc>> ReadTgAsync(string path)
    {
        List<TechLogDoc> docs = [];
        await foreach (var doc in TechLogParser.ParseJsonDumpAsync(path))
        {
            docs.Add(doc);
        }
        return docs;
    }

    [Fact]
    public async Task EventLog_PrettyArray_WithBom_ShouldReadAllDocuments()
    {
        // Такой файл пишет сам GUI при экспорте «Форматированный JSON».
        var path = PathOf("eventlog.json");
        List<EventLogDoc> source = [Ev("a"), Ev("b", "Иванов"), Ev("c")];
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(source, LogJsonContext.Pretty.ListEventLogDoc), Encoding.UTF8);

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b", "c");
        docs[1].User.Should().Be("Иванов");
        docs[0].Comment.Should().Be(source[0].Comment);
        docs[0].Date.Should().Be(source[0].Date);
    }

    [Fact]
    public async Task TechLog_PrettyArray_WithBom_ShouldReadAllDocuments()
    {
        var path = PathOf("techlog.json");
        List<TechLogDoc> source = [Tg("a"), Tg("b", "SELECT\n  '}' AS x"), Tg("c")];
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(source, LogJsonContext.Pretty.ListTechLogDoc), Encoding.UTF8);

        var docs = await ReadTgAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b", "c");
        docs[1].Sql.Should().Be("SELECT\n  '}' AS x");
        docs[0].Properties.Should().Contain("Raw", "{a}[b]");
        docs[0].Duration.Should().Be(500);
    }

    [Fact]
    public async Task EventLog_CompactArrayOnSingleLine_ShouldReadAllDocuments()
    {
        var path = PathOf("compact.json");
        List<EventLogDoc> source = [Ev("a"), Ev("b")];
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(source, LogJsonContext.Compact.ListEventLogDoc), new UTF8Encoding(false));

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task TechLog_PrettyObjectsOneAfterAnother_ShouldReadAllDocuments()
    {
        var path = PathOf("concatenated.json");
        var text = string.Join(Environment.NewLine, new[] { Tg("a"), Tg("b") }
            .Select(d => JsonSerializer.Serialize(d, LogJsonContext.Pretty.TechLogDoc)));
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));

        var docs = await ReadTgAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task EventLog_JsonLines_ShouldSkipBlankAndBrokenLines()
    {
        var path = PathOf("eventlog.jsonl");
        string[] lines =
        [
            JsonSerializer.Serialize(Ev("a"), LogJsonContext.Compact.EventLogDoc),
            "",
            "{\"id\":\"broken\",\"User\":\"обрыв строки",
            JsonSerializer.Serialize(Ev("b"), LogJsonContext.Compact.EventLogDoc),
            "{\"id\":\"broken2\",\"TransactionNumber\":",
            JsonSerializer.Serialize(Ev("c"), LogJsonContext.Compact.EventLogDoc),
            "не JSON вовсе }",
            JsonSerializer.Serialize(Ev("d"), LogJsonContext.Compact.EventLogDoc)
        ];
        await File.WriteAllLinesAsync(path, lines, Encoding.UTF8);

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public async Task EventLog_WrapperObjectWithArray_ShouldReadArrayElements()
    {
        var path = PathOf("wrapper.json");
        var items = JsonSerializer.Serialize(new List<EventLogDoc> { Ev("a"), Ev("b") }, LogJsonContext.Pretty.ListEventLogDoc);
        await File.WriteAllTextAsync(path, "{ \"meta\": [ { \"name\": \"id\" } ], \"rows\": 2, \"data\": " + items + " }", new UTF8Encoding(false));

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task TechLog_ElasticHits_ShouldReadSourceObjects()
    {
        var path = PathOf("hits.json");
        var hits = string.Join(",", new[] { Tg("a"), Tg("b") }
            .Select(d => "{\"_index\":\"techlog\",\"_source\":" + JsonSerializer.Serialize(d, LogJsonContext.Compact.TechLogDoc) + "}"));
        await File.WriteAllTextAsync(path, "{\"hits\":{\"total\":2,\"hits\":[" + hits + "]}}", new UTF8Encoding(false));

        var docs = await ReadTgAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task TechLog_Utf16File_ShouldReadAllDocuments()
    {
        // Windows PowerShell 5.1 по умолчанию сохраняет вывод в UTF-16.
        var path = PathOf("utf16.json");
        List<TechLogDoc> source = [Tg("a", "ВЫБРАТЬ 1"), Tg("b")];
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(source, LogJsonContext.Pretty.ListTechLogDoc), Encoding.Unicode);

        var docs = await ReadTgAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b");
        docs[0].Sql.Should().Be("ВЫБРАТЬ 1");
    }

    [Fact]
    public async Task EventLog_LargeArray_ShouldReadAcrossBufferBoundaries()
    {
        var path = PathOf("large.json");
        var source = Enumerable.Range(0, 5000).Select(i => Ev($"doc{i}", new string('я', i % 300))).ToList();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(source, LogJsonContext.Pretty.ListEventLogDoc), Encoding.UTF8);

        var docs = await ReadEvAsync(path);

        docs.Should().HaveCount(5000);
        docs[4999].Id.Should().Be("doc4999");
        docs[299].User.Should().Be(new string('я', 299));
    }

    [Fact]
    public async Task EventLog_StartOffset_ShouldReadFromGivenLine()
    {
        var path = PathOf("offset.jsonl");
        var first = JsonSerializer.Serialize(Ev("a"), LogJsonContext.Compact.EventLogDoc) + "\n";
        var second = JsonSerializer.Serialize(Ev("b"), LogJsonContext.Compact.EventLogDoc) + "\n";
        await File.WriteAllTextAsync(path, first + second, new UTF8Encoding(false));

        List<EventLogDoc> docs = [];
        await foreach (var doc in EventLogParser.ParseJsonDumpAsync(path, Encoding.UTF8.GetByteCount(first)))
        {
            docs.Add(doc);
        }

        docs.Select(d => d.Id).Should().Equal("b");
    }

    [Fact]
    public async Task TechLog_CancelledToken_ShouldThrowOperationCanceled()
    {
        var path = PathOf("cancel.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new List<TechLogDoc> { Tg("a") }, LogJsonContext.Pretty.ListTechLogDoc));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var _ in TechLogParser.ParseJsonDumpAsync(path, ct: cts.Token))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("dump.json", true)]
    [InlineData("dump.JSONL", true)]
    [InlineData("dump.ndjson", true)]
    [InlineData("20260913000000.lgp", false)]
    [InlineData("rphost_1234.log", false)]
    [InlineData("", false)]
    public void IsJsonFile_ShouldRecognizeJsonExtensions(string fileName, bool expected)
    {
        LogDiscovery.IsJsonFile(fileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("techlog.jsonl")]
    [InlineData("techlog.ndjson")]
    public void FindTechLogFiles_SingleJsonLinesFile_ShouldBeMarkedAsDump(string fileName)
    {
        var path = PathOf(fileName);
        File.WriteAllText(path, "{}");

        var found = LogDiscovery.FindTechLogFiles(path).ToList();

        found.Should().ContainSingle();
        found[0].FilePath.Should().Be(path);
        found[0].ProcessName.Should().Be("dump");
    }

    [Fact]
    public void FindTechLogFiles_DirectoryWithOnlyJsonLines_ShouldFindThem()
    {
        File.WriteAllText(PathOf("one.jsonl"), "{}");
        File.WriteAllText(PathOf("two.ndjson"), "{}");

        var found = LogDiscovery.FindTechLogFiles(_tempDir).Select(f => Path.GetFileName(f.FilePath)).ToList();

        found.Should().BeEquivalentTo("one.jsonl", "two.ndjson");
    }

    private const string ExtraArray = ",\"extra\":[{\"k\":1},{\"k\":[{\"z\":2}]}]}";

    private static string EvJson(string id) => JsonSerializer.Serialize(Ev(id), LogJsonContext.Compact.EventLogDoc);

    private static string EvJsonWithArray(string id) => EvJson(id)[..^1] + ExtraArray;

    [Fact]
    public async Task EventLog_RecordWithArrayOfObjects_ShouldBeRead()
    {
        var path = PathOf("foreign.ndjson");
        await File.WriteAllTextAsync(path, EvJsonWithArray("a") + "\n" + EvJsonWithArray("b") + "\n" + EvJson("c") + "\n");

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task EventLog_WrapperWithRecordsHavingArrayOfObjects_ShouldBeRead()
    {
        var path = PathOf("wrapped.json");
        await File.WriteAllTextAsync(path, "{\"total\":2,\"data\":[" + EvJsonWithArray("a") + "," + EvJsonWithArray("b") + "]}");

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task EventLog_LineBrokenInsideNestedArray_ShouldNotLoseFollowingLines()
    {
        var path = PathOf("broken_nested.ndjson");
        var broken = EvJson("x")[..^1] + ",\"extra\":[{\"k\":1";
        await File.WriteAllTextAsync(path, broken + "\n" + EvJson("b") + "\n" + EvJson("c") + "\n");

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("b", "c");
    }

    [Fact]
    public async Task TechLog_BraceOnOwnLine_ShouldBeRead()
    {
        // Скобка вложенного объекта с первой позиции строки — не признак оборванной записи.
        var path = PathOf("allman.json");
        var json = JsonSerializer.Serialize(Tg("a"), LogJsonContext.Compact.TechLogDoc)
            .Replace("\"Properties\":{", "\"Properties\":\n{");
        json.Should().Contain("\n{");
        await File.WriteAllTextAsync(path, json + "\n" + json.Replace("\"a\"", "\"b\"") + "\n");

        var docs = await ReadTgAsync(path);

        docs.Should().HaveCount(2);
        docs[0].Properties.Should().ContainKey("Usr");
    }

    [Fact]
    public async Task TechLog_RecordLargerThanInitialBuffer_ShouldBeRead()
    {
        var path = PathOf("big.ndjson");
        var sql = new string('Я', 300_000);
        var lines = new[] { Tg("a"), Tg("b", sql), Tg("c") }
            .Select(d => JsonSerializer.Serialize(d, LogJsonContext.Compact.TechLogDoc));
        await File.WriteAllTextAsync(path, string.Join("\n", lines));

        var docs = await ReadTgAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b", "c");
        docs[1].Sql.Should().Be(sql);
    }

    [Fact]
    public async Task EventLog_Utf16BigEndian_ShouldReadAllDocuments()
    {
        var path = PathOf("utf16be.json");
        await File.WriteAllTextAsync(path, EvJson("a") + "\r\n" + EvJson("b") + "\r\n", Encoding.BigEndianUnicode);

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a", "b");
        docs[0].Comment.Should().Be(Ev("a").Comment);
    }

    [Fact]
    public async Task EventLog_ClickHouseExport_WithDateColumnOnly_ShouldBeRead()
    {
        // Выгрузка таблицы ClickHouse (FORMAT JSONEachRow): дата лежит только в колонке Date, поля DateTime нет.
        var path = PathOf("clickhouse.json");
        const string line1 = "{\"id\":\"a1\",\"Date\":\"2026-10-05 07:00:00.000\",\"DateFormatted\":\"2026-10-05 07:00:00\",\"Event\":\"_$Session$.Start\",\"User\":\"Admin\",\"Metadata\":\"\",\"Tran\":\"-\",\"Application\":\"HTTPServiceConnection\",\"Comment\":\"строка 1\\nстрока 2\",\"Severity\":\"Информация\",\"Server\":\"SRV-APP01\",\"FileSize\":13181875111,\"Id\":\"a1\",\"TransactionDate\":null,\"TransactionNumber\":0,\"MetadataUuid\":\"\",\"AddPort\":\"\"}";
        var line2 = line1.Replace("\"a1\"", "\"a2\"").Replace("07:00:00", "07:00:01");
        await File.WriteAllTextAsync(path, line1 + "\n" + line2 + "\n", new UTF8Encoding(false));

        var docs = await ReadEvAsync(path);

        docs.Select(d => d.Id).Should().Equal("a1", "a2");
        docs[0].Date.Should().Be(new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc));
        docs[1].Date.Should().Be(new DateTime(2026, 10, 5, 7, 0, 1, DateTimeKind.Utc));
        docs[0].Server.Should().Be("SRV-APP01");
        docs[0].FileSize.Should().Be(13181875111);
    }

    [Fact]
    public async Task TechLog_ClickHouseExport_WithDateColumnOnly_ShouldBeRead()
    {
        var path = PathOf("clickhouse_tg.json");
        var json = JsonSerializer.Serialize(Tg("a"), LogJsonContext.Compact.TechLogDoc);
        var start = json.IndexOf("\"DateTime\":", StringComparison.Ordinal);
        var end = json.IndexOf(',', start) + 1;
        var withoutDateTime = json.Remove(start, end - start);
        withoutDateTime.Should().Contain("\"Date\":").And.NotContain("\"DateTime\":");
        await File.WriteAllTextAsync(path, withoutDateTime + "\n");

        var docs = await ReadTgAsync(path);

        docs.Should().ContainSingle();
        docs[0].Date.Should().Be(Tg("a").Date);
    }

    [Fact]
    public async Task EventLog_StartOffsetInsideRecord_ShouldReadFromNextRecord()
    {
        var path = PathOf("offset.ndjson");
        await File.WriteAllTextAsync(path, EvJson("a") + "\n" + EvJson("b") + "\n" + EvJson("c") + "\n", new UTF8Encoding(false));

        List<EventLogDoc> docs = [];
        await foreach (var doc in EventLogParser.ParseJsonDumpAsync(path, startOffset: 40))
        {
            docs.Add(doc);
        }

        docs.Select(d => d.Id).Should().Equal("b", "c");
    }
}
