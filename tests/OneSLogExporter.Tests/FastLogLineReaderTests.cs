using System.Text;
using FluentAssertions;
using OneSLogExporter.Core.Parsers;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class FastLogLineReaderTests
{
    [Fact]
    public async Task ReadLineAsync_WithUtf8Bom_ShouldNotIncludeBomInReturnedString()
    {
        var rawData = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("{\"Event\":\"TEST\",\"Level\":\"Information\"}\n{\"Event\":\"TEST2\"}\n"))
            .ToArray();

        using var ms = new MemoryStream(rawData);
        using var reader = new FastLogLineReader(ms);

        var line1 = await reader.ReadLineAsync();
        line1.Should().NotBeNull();
        line1!.StartsWith('\uFEFF').Should().BeFalse();
        line1.Should().StartWith("{\"Event\":\"TEST\"");

        var line2 = await reader.ReadLineAsync();
        line2.Should().NotBeNull();
        line2.Should().StartWith("{\"Event\":\"TEST2\"");
    }

    [Fact]
    public async Task ReadLineAsync_EmbeddedBomInLine_ShouldStripLeadingBom()
    {
        var content = "\uFEFF{\"Event\":\"EMBEDDED_BOM\"}\n";
        var rawData = Encoding.UTF8.GetBytes(content);

        using var ms = new MemoryStream(rawData);
        using var reader = new FastLogLineReader(ms);

        var line = await reader.ReadLineAsync();
        line.Should().NotBeNull();
        line!.StartsWith('\uFEFF').Should().BeFalse();
        line.Should().StartWith("{\"Event\":\"EMBEDDED_BOM\"");
    }

    [Fact]
    public async Task Reset_ShouldRepositionStreamAndAllowRereading()
    {
        var content = "Line1\nLine2\n";
        var rawData = Encoding.UTF8.GetBytes(content);

        using var ms = new MemoryStream(rawData);
        using var reader = new FastLogLineReader(ms);

        var l1 = await reader.ReadLineAsync();
        l1.Should().Be("Line1");

        reader.Reset(0);
        var rereadL1 = await reader.ReadLineAsync();
        rereadL1.Should().Be("Line1");
    }
}
