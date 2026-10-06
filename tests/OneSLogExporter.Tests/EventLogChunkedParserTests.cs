using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using System.Text;
using Xunit;

namespace OneSLogExporter.Tests;

/// <summary>
/// Инкрементальный разбор .lgp: позиции возобновления, незавершённая запись в конце файла, пропуск известных id.
/// </summary>
public sealed class EventLogChunkedParserTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public EventLogChunkedParserTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"lgp_{Guid.NewGuid():N}", "1Cv8Log");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "20260817120000.lgp");
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static string Entry(string date, string comment) =>
        "{" + date + ",N,\r\n{0,0},1,1,1,1,1,I,\"" + comment + "\",1,\r\n{\"U\"},\"\",1,1,0,1,0,\r\n{0}\r\n},\r\n";

    private static async Task<(List<EventLogDoc> Docs, List<long> Resumes, long Final)> ParseAsync(
        string file, long offset, int batchSize = 1000, Func<string, bool>? isKnownId = null)
    {
        var docs = new List<EventLogDoc>();
        var resumes = new List<long>();
        var final = await EventLogParser.ParseLogFromOffsetChunkedAsync(
            file,
            new LgfDictionary(),
            offset,
            (batch, resume) =>
            {
                docs.AddRange(batch);
                resumes.Add(resume);
                return ValueTask.CompletedTask;
            },
            batchSize,
            filterEmptyTransactions: false,
            isKnownId: isKnownId);
        return (docs, resumes, final);
    }

    [Fact]
    public async Task Chunked_IncompleteTrailingEntry_ShouldBeHeldBackUntilCompleted()
    {
        var e1 = Entry("20260817120001", "one");
        var e2 = Entry("20260817120002", "two");
        var e3 = Entry("20260817120003", "three");
        var e3Head = e3[..(e3.IndexOf("\"three\"", StringComparison.Ordinal) + 8)];
        await File.WriteAllTextAsync(_file, e1 + e2 + e3Head, new UTF8Encoding(false));

        var (docs, _, pos) = await ParseAsync(_file, 0);

        docs.Select(d => d.Comment).Should().Equal("one", "two");
        pos.Should().Be(Encoding.UTF8.GetByteCount(e1 + e2));

        await File.AppendAllTextAsync(_file, e3[e3Head.Length..], new UTF8Encoding(false));
        var (docs2, _, pos2) = await ParseAsync(_file, pos);

        docs2.Select(d => d.Comment).Should().Equal("three");
        pos2.Should().Be(new FileInfo(_file).Length);
    }

    [Fact]
    public async Task Chunked_TruncatedEntryStartLine_ShouldNotBeSkipped()
    {
        var e1 = Entry("20260817120001", "one");
        var e2 = Entry("20260817120002", "two");
        await File.WriteAllTextAsync(_file, e1 + "{2026081712", new UTF8Encoding(false));

        var (docs, _, pos) = await ParseAsync(_file, 0);

        docs.Select(d => d.Comment).Should().Equal("one");
        pos.Should().Be(Encoding.UTF8.GetByteCount(e1));

        await File.WriteAllTextAsync(_file, e1 + e2, new UTF8Encoding(false));
        var (docs2, _, _) = await ParseAsync(_file, pos);

        docs2.Select(d => d.Comment).Should().Equal("two");
    }

    [Fact]
    public async Task Chunked_ResumeOffsets_ShouldPointPastEachBatch()
    {
        var e1 = Entry("20260817120001", "one");
        var e2 = Entry("20260817120002", "two");
        var e3 = Entry("20260817120003", "three");
        await File.WriteAllTextAsync(_file, e1 + e2 + e3, new UTF8Encoding(false));

        var (docs, resumes, _) = await ParseAsync(_file, 0, batchSize: 2);

        docs.Should().HaveCount(3);
        resumes.Should().Equal(Encoding.UTF8.GetByteCount(e1 + e2), Encoding.UTF8.GetByteCount(e1 + e2 + e3));

        // Продолжение с позиции первой пачки даёт ровно оставшуюся запись.
        var (rest, _, _) = await ParseAsync(_file, resumes[0]);
        rest.Select(d => d.Comment).Should().Equal("three");
    }

    [Fact]
    public async Task Chunked_KnownIds_ShouldBeSkippedAndIdsDeterministic()
    {
        var content = Entry("20260817120001", "one") + Entry("20260817120002", "two");
        await File.WriteAllTextAsync(_file, content, new UTF8Encoding(false));

        var (first, _, _) = await ParseAsync(_file, 0);
        var (again, _, _) = await ParseAsync(_file, 0);
        again.Select(d => d.Id).Should().Equal(first.Select(d => d.Id));
        first.Select(d => d.Id).Should().OnlyHaveUniqueItems();

        var known = new HashSet<string> { first[0].Id };
        var (filtered, _, pos) = await ParseAsync(_file, 0, isKnownId: known.Contains);

        filtered.Select(d => d.Comment).Should().Equal("two");
        pos.Should().Be(new FileInfo(_file).Length);
    }

    [Fact]
    public async Task Chunked_SameEntryInAnotherFileOfSameBase_ShouldGetSameId()
    {
        var entry = Entry("20260817120001", "one");
        var other = Path.Combine(_dir, "20260817130000.lgp");
        await File.WriteAllTextAsync(_file, entry, new UTF8Encoding(false));
        await File.WriteAllTextAsync(other, entry.Replace("\r\n", "\n"), new UTF8Encoding(false));

        var (a, _, _) = await ParseAsync(_file, 0);
        var (b, _, _) = await ParseAsync(other, 0);

        b.Single().Id.Should().Be(a.Single().Id);
        EventLogParser.GetIdScope(_file).Should().Be(Path.GetFileName(Path.GetDirectoryName(_dir)));
    }

    [Fact]
    public async Task Chunked_StartOffsetBeyondLength_ShouldReturnLength()
    {
        await File.WriteAllTextAsync(_file, Entry("20260817120001", "one"), new UTF8Encoding(false));
        var length = new FileInfo(_file).Length;

        var (docs, _, pos) = await ParseAsync(_file, length + 100);

        docs.Should().BeEmpty();
        pos.Should().Be(length);
    }
}
