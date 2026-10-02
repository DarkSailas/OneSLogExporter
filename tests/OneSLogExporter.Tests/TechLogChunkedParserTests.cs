using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using System.Text;
using Xunit;

namespace OneSLogExporter.Tests;

/// <summary>
/// Инкрементальный разбор ТЖ: позиции возобновления, оборванная последняя строка, пропуск известных id.
/// </summary>
public sealed class TechLogChunkedParserTests : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"26073008_{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    private static string Line(string time, string user) =>
        $"{time}-31985,DBMSSQL,5,p:processName=DemoDb,t:connectID=1,Usr={user},Context='Модуль : 1'\r\n";

    private async Task<(List<TechLogDoc> Docs, List<long> Resumes, long Final)> ParseAsync(
        long offset, int batchSize = 1000, Func<string, bool>? isKnownId = null)
    {
        var docs = new List<TechLogDoc>();
        var resumes = new List<long>();
        var final = await TechLogParser.ParseFileFromOffsetChunkedAsync(
            _file,
            "rphost",
            "9000",
            offset,
            (batch, resume) =>
            {
                docs.AddRange(batch);
                resumes.Add(resume);
                return ValueTask.CompletedTask;
            },
            batchSize,
            filterEmptyEvents: false,
            isKnownId: isKnownId);
        return (docs, resumes, final);
    }

    [Fact]
    public async Task Chunked_UnterminatedLastLine_ShouldBeHeldBack()
    {
        var l1 = Line("23:48.000001", "A");
        var l2 = Line("23:49.000001", "B");
        var l3 = Line("23:50.000001", "C");
        await File.WriteAllTextAsync(_file, l1 + l2 + l3[..20], Utf8);

        var (docs, _, pos) = await ParseAsync(0);

        docs.Select(d => d.User).Should().Equal("A", "B");
        pos.Should().Be(Utf8.GetByteCount(l1 + l2));

        await File.AppendAllTextAsync(_file, l3[20..], Utf8);
        var (docs2, _, pos2) = await ParseAsync(pos);

        docs2.Select(d => d.User).Should().Equal("C");
        pos2.Should().Be(new FileInfo(_file).Length);
    }

    [Fact]
    public async Task Chunked_ResumeOffsets_ShouldPointAtNextUnreadEvent()
    {
        var l1 = Line("23:48.000001", "A");
        var l2 = Line("23:49.000001", "B");
        var l3 = Line("23:50.000001", "C");
        await File.WriteAllTextAsync(_file, l1 + l2 + l3, Utf8);

        var (docs, resumes, _) = await ParseAsync(0, batchSize: 2);

        docs.Should().HaveCount(3);
        resumes.Should().Equal(Utf8.GetByteCount(l1 + l2), Utf8.GetByteCount(l1 + l2 + l3));

        var (rest, _, _) = await ParseAsync(resumes[0]);
        rest.Select(d => d.User).Should().Equal("C");
    }

    [Fact]
    public async Task Chunked_KnownIds_ShouldBeSkippedAndIdsMatchParseBlock()
    {
        var l1 = Line("23:48.000001", "A");
        var l2 = Line("23:49.000001", "B");
        await File.WriteAllTextAsync(_file, l1 + l2, Utf8);

        var (first, _, _) = await ParseAsync(0);
        var direct = TechLogParser.ParseBlock(l1.TrimEnd(), 2026, 7, 30, 8, "rphost", "9000");
        first[0].Id.Should().Be(direct!.Id);
        first.Select(d => d.Id).Should().OnlyHaveUniqueItems();

        var known = new HashSet<string> { first[0].Id };
        var (filtered, _, pos) = await ParseAsync(0, isKnownId: known.Contains);

        filtered.Select(d => d.User).Should().Equal("B");
        pos.Should().Be(new FileInfo(_file).Length);
    }

    [Fact]
    public void ParseBlock_SameEventDifferentProcessOrHour_ShouldDifferInId()
    {
        var line = Line("23:48.000001", "A").TrimEnd();
        var a = TechLogParser.ParseBlock(line, 2026, 7, 30, 8, "rphost", "9000")!.Id;

        TechLogParser.ParseBlock(line, 2026, 7, 30, 8, "rphost", "9000")!.Id.Should().Be(a);
        TechLogParser.ParseBlock(line, 2026, 7, 30, 9, "rphost", "9000")!.Id.Should().NotBe(a);
        TechLogParser.ParseBlock(line, 2026, 7, 30, 8, "rphost", "9001")!.Id.Should().NotBe(a);
    }
}
