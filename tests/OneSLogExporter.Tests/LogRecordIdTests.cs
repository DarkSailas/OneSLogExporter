using FluentAssertions;
using OneSLogExporter.Core.Parsers;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class LogRecordIdTests
{
    [Fact]
    public void Compute_SameInput_ShouldBeDeterministic()
    {
        var a = LogRecordId.Compute("scope", "{20260817123045,N,\n{0,0},1,1\n},");
        var b = LogRecordId.Compute("scope", "{20260817123045,N,\n{0,0},1,1\n},");

        a.Should().Be(b);
        a.Should().HaveLength(32).And.MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Compute_CrLfAndLf_ShouldGiveSameId()
    {
        var lf = LogRecordId.Compute("s", "line1\nline2");
        var crlf = LogRecordId.Compute("s", "line1\r\nline2\r\n");

        crlf.Should().Be(lf);
    }

    [Fact]
    public void Compute_DifferentScopeOrContent_ShouldDiffer()
    {
        var baseId = LogRecordId.Compute("a", "content");

        LogRecordId.Compute("b", "content").Should().NotBe(baseId);
        LogRecordId.Compute("a", "content2").Should().NotBe(baseId);
        LogRecordId.Compute(null, "content").Should().NotBe(baseId);
    }

    [Fact]
    public void Compute_LargeBlock_ShouldWork()
    {
        var big = new string('ж', 200_000);
        var id = LogRecordId.Compute("s", big);

        id.Should().HaveLength(32);
        LogRecordId.Compute("s", big + "x").Should().NotBe(id);
    }

    [Fact]
    public void ToKey_ShouldBeStableAndDistinguish()
    {
        LogRecordId.ToKey("abc").Should().Be(LogRecordId.ToKey("abc"));
        LogRecordId.ToKey("abc").Should().NotBe(LogRecordId.ToKey("abd"));
    }
}
