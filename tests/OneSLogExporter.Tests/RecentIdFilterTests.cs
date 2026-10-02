using FluentAssertions;
using OneSLogExporter.Core.State;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class RecentIdFilterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dedup_{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Add_ThenContains_ShouldReturnTrue()
    {
        var filter = new RecentIdFilter(_path, 10);

        filter.Contains("id1").Should().BeFalse();
        filter.Add("id1").Should().BeTrue();
        filter.Add("id1").Should().BeFalse();
        filter.Contains("id1").Should().BeTrue();
        filter.Count.Should().Be(1);
    }

    [Fact]
    public void Add_OverCapacity_ShouldEvictOldest()
    {
        var filter = new RecentIdFilter(_path, 3);
        filter.Add("a");
        filter.Add("b");
        filter.Add("c");
        filter.Add("d");

        filter.Count.Should().Be(3);
        filter.Contains("a").Should().BeFalse();
        filter.Contains("b").Should().BeTrue();
        filter.Contains("d").Should().BeTrue();
    }

    [Fact]
    public async Task SaveAndLoad_ShouldRoundTripInOrder()
    {
        var filter = new RecentIdFilter(_path, 3);
        filter.Add("a");
        filter.Add("b");
        filter.Add("c");
        await filter.SaveAsync();

        var restored = new RecentIdFilter(_path, 3);
        await restored.LoadAsync();

        restored.Count.Should().Be(3);
        restored.Contains("a").Should().BeTrue();
        restored.Add("d");
        restored.Contains("a").Should().BeFalse("FIFO order must survive persistence");
        restored.Contains("b").Should().BeTrue();
    }

    [Fact]
    public async Task Load_WithSmallerCapacity_ShouldKeepNewest()
    {
        var filter = new RecentIdFilter(_path, 5);
        foreach (var id in new[] { "a", "b", "c", "d", "e" }) filter.Add(id);
        await filter.SaveAsync();

        var restored = new RecentIdFilter(_path, 2);
        await restored.LoadAsync();

        restored.Count.Should().Be(2);
        restored.Contains("d").Should().BeTrue();
        restored.Contains("e").Should().BeTrue();
        restored.Contains("c").Should().BeFalse();
    }

    [Fact]
    public async Task Load_CorruptFile_ShouldStartEmpty()
    {
        await File.WriteAllBytesAsync(_path, [1, 2, 3]);
        var filter = new RecentIdFilter(_path, 10);

        await filter.LoadAsync();

        filter.Count.Should().Be(0);
    }

    [Fact]
    public async Task Load_MissingFile_ShouldStartEmpty()
    {
        var filter = new RecentIdFilter(_path, 10);
        await filter.LoadAsync();
        filter.Count.Should().Be(0);
    }
}
