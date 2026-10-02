using FluentAssertions;
using OneSLogExporter.Core.State;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class StateTrackerTests : IDisposable
{
    private readonly string _tempFile;

    public StateTrackerTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"state_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            try { File.Delete(_tempFile); } catch { }
        }
    }

    [Fact]
    public async Task ConcurrentOperations_ShouldNotThrow_CollectionModifiedException()
    {
        // Arrange
        var tracker = new StateTracker(_tempFile);
        await tracker.LoadAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = cts.Token;

        // Act - параллельные задачи обновления позиций и сохранения
        var updateTask1 = Task.Run(() =>
        {
            var i = 0L;
            while (!ct.IsCancellationRequested)
            {
                tracker.MarkFilePosition($@"C:\Logs\techlog_{i % 100}.log", i, 1000);
                i++;
            }
        }, ct);

        var updateTask2 = Task.Run(() =>
        {
            var i = 0L;
            while (!ct.IsCancellationRequested)
            {
                tracker.MarkFilePosition($@"C:\Logs\eventlog_{i % 100}.lgp", i, 2000);
                i++;
            }
        }, ct);

        var saveTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await tracker.SaveAsync(CancellationToken.None);
                await Task.Delay(10, CancellationToken.None);
            }
        }, ct);

        var readTask = Task.Run(() =>
        {
            var i = 0L;
            while (!ct.IsCancellationRequested)
            {
                tracker.HasFileGrown($@"C:\Logs\techlog_{i % 100}.log", 500);
                i++;
            }
        }, ct);

        // Assert
        var act = async () => await Task.WhenAll(updateTask1, updateTask2, saveTask, readTask);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void HasFileGrown_ForLgd_ShouldCheckPhysicalSizeChange()
    {
        // Arrange
        var tracker = new StateTracker(_tempFile);
        var lgdPath = @"C:\1Cv8Log\1Cv8.lgd";

        // Act & Assert 1: Новый файл
        tracker.HasFileGrown(lgdPath, 100_000).Should().BeTrue();

        // Фиксируем rowID = 5_000_000, но размер файла = 100_000
        tracker.MarkFilePosition(lgdPath, 5_000_000, 100_000);

        // Размер не изменился -> false (даже если 100_000 < 5_000_000)
        tracker.HasFileGrown(lgdPath, 100_000).Should().BeFalse();

        // Размер вырос -> true
        tracker.HasFileGrown(lgdPath, 105_000).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_CalledTwice_ShouldNotDiscardInMemoryPositions()
    {
        var tracker = new StateTracker(_tempFile);
        await tracker.LoadAsync();
        tracker.MarkFilePosition(@"C:\Logs\a.log", 500, 1000);

        // Второй воркер вызывает LoadAsync на общем экземпляре.
        await tracker.LoadAsync();

        tracker.GetLastPosition(@"C:\Logs\a.log").Should().Be(500);
    }

    [Fact]
    public async Task SaveThenLoad_ShouldRoundTripWithoutTempLeftovers()
    {
        var tracker = new StateTracker(_tempFile);
        await tracker.LoadAsync();
        tracker.MarkFilePosition(@"C:\Logs\a.log", 500, 1000);
        await tracker.SaveAsync();

        File.Exists(_tempFile + ".tmp").Should().BeFalse();

        var restored = new StateTracker(_tempFile);
        await restored.LoadAsync();
        restored.GetLastPosition(@"C:\Logs\a.log").Should().Be(500);
        restored.StateFilePath.Should().Be(_tempFile);
    }

    [Fact]
    public async Task LoadAsync_CorruptFile_ShouldKeepBackupAndStartEmpty()
    {
        await File.WriteAllTextAsync(_tempFile, "{ \"broken\": ");
        var tracker = new StateTracker(_tempFile);

        await tracker.LoadAsync();

        tracker.HasTrackedState(@"C:\Logs\a.log").Should().BeFalse();
        var backups = Directory.GetFiles(Path.GetDirectoryName(_tempFile)!, Path.GetFileName(_tempFile) + ".corrupt-*");
        try
        {
            backups.Should().ContainSingle();
        }
        finally
        {
            foreach (var b in backups) File.Delete(b);
        }
    }
}