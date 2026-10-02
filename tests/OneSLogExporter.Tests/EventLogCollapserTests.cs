using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Services;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class EventLogCollapserTests
{
    private const string Meta = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTime Base = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static EventLogDoc Doc(string id, int second, string? meta = Meta, string ev = "Данные. Изменение", string user = "u1") => new()
    {
        Id = id,
        Date = Base.AddSeconds(second),
        DateFormatted = Base.AddSeconds(second).ToString("yyyy-MM-dd HH:mm:ss"),
        Event = ev,
        UserUuid = user,
        MetadataUuid = meta,
    };

    [Fact]
    public void Collapse_SameMetadataEventUserInWindow_ShouldKeepFirstWithCount()
    {
        var batch = new[] { Doc("a", 1), Doc("b", 5), Doc("c", 59) };

        var result = EventLogCollapser.Collapse(batch, TimeSpan.FromSeconds(60));

        result.Should().ContainSingle();
        result[0].Id.Should().Be("a");
        result[0].RepeatCount.Should().Be(3);
    }

    [Fact]
    public void Collapse_DifferentWindowEventOrUser_ShouldStaySeparate()
    {
        var batch = new[]
        {
            Doc("a", 1),
            Doc("b", 61),                       // следующее окно
            Doc("c", 2, ev: "Данные. Удаление"),
            Doc("d", 3, user: "u2"),
        };

        var result = EventLogCollapser.Collapse(batch, TimeSpan.FromSeconds(60));

        result.Select(d => d.Id).Should().Equal("a", "b", "c", "d");
        result.Should().OnlyContain(d => d.RepeatCount == 1);
    }

    [Fact]
    public void Collapse_WithoutMetadataUuid_ShouldNotCollapse()
    {
        var batch = new[] { Doc("a", 1, meta: null), Doc("b", 2, meta: null), Doc("c", 3, meta: "") };

        var result = EventLogCollapser.Collapse(batch, TimeSpan.FromSeconds(60));

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Collapse_ShouldPreserveOrderAndTotalCount()
    {
        var batch = new[] { Doc("a", 1), Doc("x", 2, meta: null), Doc("b", 3), Doc("c", 70) };

        var result = EventLogCollapser.Collapse(batch, TimeSpan.FromSeconds(60));

        result.Select(d => d.Id).Should().Equal("a", "x", "c");
        result.Sum(d => d.RepeatCount).Should().Be(batch.Length);
    }
}
