using FluentAssertions;
using OneSLogExporter.Core.Models;
using Xunit;

namespace OneSLogExporter.Tests;

public class SingleLineTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInput_ShouldGiveEmptyString(string? input)
    {
        SingleLineText.From(input).Should().BeEmpty();
    }

    [Fact]
    public void ShortSingleLine_ShouldBeReturnedAsIs()
    {
        var text = "Проведение документа";

        SingleLineText.From(text).Should().BeSameAs(text);
    }

    [Fact]
    public void LineBreaksAndTabs_ShouldBecomeSingleSpaces()
    {
        SingleLineText.From("первая\r\nвторая\n\n\tтретья\rчетвёртая")
            .Should().Be("первая вторая третья четвёртая");
    }

    [Fact]
    public void LeadingAndTrailingLineBreaks_ShouldBeDropped()
    {
        SingleLineText.From("\r\n\tтекст\n").Should().Be("текст");
    }

    [Fact]
    public void LongText_ShouldBeCutWithEllipsis()
    {
        var result = SingleLineText.From(new string('a', 1000), maxLength: 10);

        result.Should().Be(new string('a', 10) + "…");
    }

    [Fact]
    public void TextOfExactlyMaxLength_ShouldNotGetEllipsis()
    {
        SingleLineText.From("ab\ncd", maxLength: 5).Should().Be("ab cd");
    }

    [Fact]
    public void LongMultilineText_ShouldBeFlattenedAndCut()
    {
        var text = string.Join("\n", Enumerable.Repeat("строка", 5000));

        var result = SingleLineText.From(text, maxLength: 20);

        result.Should().Be("строка строка строка…");
    }

    [Fact]
    public void SurrogatePairAtCutPoint_ShouldNotBeSplit()
    {
        var result = SingleLineText.From("ab😀cd", maxLength: 3);

        result.Should().Be("ab…");
    }
}
