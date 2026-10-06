using System.Globalization;
using FluentAssertions;
using OneSLogExporter.Core.Models;
using Xunit;

namespace OneSLogExporter.Tests;

public class TimeFilterInputTests
{
    [Theory]
    [InlineData("00:14", 0, 14, 0)]
    [InlineData("04.05", 4, 5, 0)]
    [InlineData("4.5", 4, 5, 0)]
    [InlineData("04,05", 4, 5, 0)]
    [InlineData("4", 4, 0, 0)]
    [InlineData("16", 16, 0, 0)]
    [InlineData("405", 4, 5, 0)]
    [InlineData("1613", 16, 13, 0)]
    [InlineData("161350", 16, 13, 50)]
    [InlineData("16:13:50", 16, 13, 50)]
    [InlineData("16.13.50", 16, 13, 50)]
    [InlineData(" 16:13 ", 16, 13, 0)]
    [InlineData("16:", 16, 0, 0)]
    [InlineData("23:59:59", 23, 59, 59)]
    public void TimeOfDay_ShouldBeReadInAnyForm(string input, int hours, int minutes, int seconds)
    {
        TimeFilterInput.TryParse(input, out var time, out var exact).Should().BeTrue();

        time.Should().Be(new TimeSpan(hours, minutes, seconds));
        exact.Should().Be(default(DateTime), "время суток не должно превращаться в дату");
    }

    [Theory]
    [InlineData("16:13:50.875")]
    [InlineData("16.13.50.875")]
    [InlineData("16:13:50,875")]
    public void Milliseconds_ShouldBeKept(string input)
    {
        TimeFilterInput.TryParse(input, out var time, out _).Should().BeTrue();

        time.Should().Be(new TimeSpan(0, 16, 13, 50, 875));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("12:30:60")]
    [InlineData("1234567")]
    [InlineData("12:345")]
    [InlineData("16:13:50.x")]
    [InlineData("-1:00")]
    public void WrongInput_ShouldBeRejected(string? input)
    {
        TimeFilterInput.TryParseTimeOfDay(input, out _).Should().BeFalse();
        TimeFilterInput.TryParse(input, out _, out _, CultureInfo.InvariantCulture).Should().BeFalse();
    }

    [Fact]
    public void IsoDateWithTime_ShouldGiveExactDate()
    {
        TimeFilterInput.TryParse("2026-08-17 16:13:00", out var time, out var exact, CultureInfo.InvariantCulture)
            .Should().BeTrue();

        exact.Should().Be(new DateTime(2026, 8, 17, 16, 13, 0));
        time.Should().Be(new TimeSpan(16, 13, 0));
    }

    [Fact]
    public void RussianDateWithTime_ShouldGiveExactDate()
    {
        TimeFilterInput.TryParse("17.08.2026 16:13", out _, out var exact, CultureInfo.GetCultureInfo("ru-RU"))
            .Should().BeTrue();

        exact.Should().Be(new DateTime(2026, 8, 17, 16, 13, 0));
    }

    [Fact]
    public void RussianDateWithoutTime_ShouldGiveExactDate()
    {
        TimeFilterInput.TryParse("17.08.2026", out _, out var exact, CultureInfo.GetCultureInfo("ru-RU"))
            .Should().BeTrue();

        exact.Should().Be(new DateTime(2026, 8, 17));
    }

    [Theory]
    [InlineData("04.05", "04:05")]
    [InlineData("4", "04:00")]
    [InlineData("405", "04:05")]
    [InlineData("1613", "16:13")]
    [InlineData("16:13:00", "16:13")]
    [InlineData("161350", "16:13:50")]
    [InlineData("16.13.50.875", "16:13:50.875")]
    [InlineData(" 00:14 ", "00:14")]
    [InlineData("2026-08-17 16:13", "2026-08-17 16:13")]
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_ShouldGiveClockForm(string? input, string expected)
    {
        TimeFilterInput.Normalize(input).Should().Be(expected);
    }
}
