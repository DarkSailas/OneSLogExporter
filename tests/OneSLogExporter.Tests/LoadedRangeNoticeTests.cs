using FluentAssertions;
using OneSLogExporter.Core.Models;
using Xunit;

namespace OneSLogExporter.Tests;

public class LoadedRangeNoticeTests
{
    private static readonly DateTime LoadedAt = new(2026, 10, 6, 12, 41, 30);
    private static readonly DateTime Day4 = new(2026, 10, 4);
    private static readonly DateTime Day3 = new(2026, 10, 3);

    [Fact]
    public void Build_NothingLoaded_ShouldBeEmpty()
    {
        var notice = LoadedRangeNotice.Build(null, null, null, Day3, Day3, "РАСПАРСИТЬ ЖР");

        notice.Text.Should().BeEmpty();
        notice.IsWarning.Should().BeFalse();
    }

    [Fact]
    public void Build_NoDateFilter_ShouldTellWhenDataWasRead()
    {
        var notice = LoadedRangeNotice.Build(Day4.AddHours(1), Day4.AddHours(23), LoadedAt, null, null, "РАСПАРСИТЬ ЖР");

        notice.IsWarning.Should().BeFalse();
        notice.Text.Should().Contain("04.10.2026").And.Contain("12:41").And.Contain("«РАСПАРСИТЬ ЖР»");
    }

    [Fact]
    public void Build_FilterInsideLoadedDays_ShouldNotWarn()
    {
        var notice = LoadedRangeNotice.Build(Day3.AddHours(5), Day4.AddHours(23), LoadedAt, Day4, Day4, "РАСПАРСИТЬ ЖР");

        notice.IsWarning.Should().BeFalse();
        notice.Text.Should().Contain("03.10.2026 — 04.10.2026");
    }

    [Fact]
    public void Build_FilterOutsideLoadedDays_ShouldWarn()
    {
        var notice = LoadedRangeNotice.Build(Day4.AddHours(1), Day4.AddHours(23), LoadedAt, Day3, Day3, "РАСПАРСИТЬ ЖР");

        notice.IsWarning.Should().BeTrue();
        notice.Text.Should().Contain("04.10.2026").And.Contain("«РАСПАРСИТЬ ЖР»");
    }

    [Theory]
    [InlineData(3, null)]
    [InlineData(null, 5)]
    [InlineData(3, 5)]
    public void Build_FilterPartlyOutsideLoadedDays_ShouldWarn(int? fromDay, int? toDay)
    {
        DateTime? from = fromDay is { } f ? new DateTime(2026, 10, f) : null;
        DateTime? to = toDay is { } t ? new DateTime(2026, 10, t) : null;

        var notice = LoadedRangeNotice.Build(Day4.AddHours(1), Day4.AddHours(23), LoadedAt, from, to, "РАСПАРСИТЬ ЖР");

        notice.IsWarning.Should().BeTrue();
    }

    [Fact]
    public void Build_LoadedWithoutRecords_ShouldWarnOnlyWithDateFilter()
    {
        LoadedRangeNotice.Build(null, null, LoadedAt, null, null, "РАСПАРСИТЬ ТЖ").IsWarning.Should().BeFalse();

        var notice = LoadedRangeNotice.Build(null, null, LoadedAt, Day3, Day3, "РАСПАРСИТЬ ТЖ");
        notice.IsWarning.Should().BeTrue();
        notice.Text.Should().Contain("«РАСПАРСИТЬ ТЖ»");
    }
}
