using FluentAssertions;
using OneSLogExporter.Core.State;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class DuplicateGateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"gate_{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Disabled_ShouldNeverReportDuplicates()
    {
        var gate = DuplicateGate.Create(false, _path, 10);

        gate.Predicate.Should().BeNull();
        gate.IsDuplicate("a").Should().BeFalse();
        gate.IsDuplicate("a").Should().BeFalse();
    }

    [Fact]
    public void Pending_ShouldCatchDuplicatesWithinBatch_AndCommitShouldRemember()
    {
        var gate = DuplicateGate.Create(true, _path, 10);

        gate.IsDuplicate("a").Should().BeFalse();
        gate.IsDuplicate("a").Should().BeTrue("same id twice in one unsent batch");

        gate.Commit(["a"]);
        gate.IsDuplicate("a").Should().BeTrue("already sent");
        gate.TakeSkippedCount().Should().Be(2);
        gate.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void ResetPending_AfterFailedSend_ShouldAllowRetry()
    {
        var gate = DuplicateGate.Create(true, _path, 10);

        gate.IsDuplicate("a").Should().BeFalse();
        gate.ResetPending();

        gate.IsDuplicate("a").Should().BeFalse("the batch was never sent");
    }

    [Fact]
    public async Task SaveAndLoad_ShouldPersistCommittedIds()
    {
        var gate = DuplicateGate.Create(true, _path, 10);
        gate.IsDuplicate("a");
        gate.Commit(["a"]);
        await gate.SaveAsync(CancellationToken.None);

        var restored = DuplicateGate.Create(true, _path, 10);
        await restored.LoadAsync(CancellationToken.None);

        restored.IsDuplicate("a").Should().BeTrue();
    }
}
