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
}