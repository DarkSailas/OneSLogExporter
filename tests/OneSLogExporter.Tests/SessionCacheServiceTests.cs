using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Services;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class SessionCacheServiceTests : IDisposable
{
    private readonly string _testTempDir;

    public SessionCacheServiceTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "OneSTestTemp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
                Directory.Delete(_testTempDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task EventLogCache_InsertAndStream_ShouldPreserveAllFieldsAndCleanupOnDispose()
    {
        // Arrange
        var cache = SessionCacheService.CreateEventLogCache(_testTempDir);
        var dbPath = cache.DbFilePath;
        File.Exists(dbPath).Should().BeTrue();

        var docs = new List<EventLogDoc>
        {
            new()
            {
                Id = "el_1",
                Date = new DateTime(2026, 9, 4, 11, 0, 0, DateTimeKind.Utc),
                DateFormatted = "2026-09-04 11:00:00",
                Event = "_$Session$_.Start",
                User = "Администратор",
                Comment = "Вход в систему",
                Importance = "Information",
                Computer = "SRV-1C-01",
                AppTypeName = "1CV8C",
                FileSize = 1024,
                FileSizeFormatted = "1.0 КБ"
            },
            new()
            {
                Id = "el_2",
                Date = new DateTime(2026, 9, 4, 11, 1, 0, DateTimeKind.Utc),
                DateFormatted = "2026-09-04 11:01:00",
                Event = "_$Data$_.Post",
                User = "Бухгалтер",
                Comment = "Проведение документа",
                Importance = "Warning",
                Computer = "SRV-1C-02",
                AppTypeName = "1CV8",
                FileSize = 2048,
                FileSizeFormatted = "2.0 КБ"
            }
        };

        // Act
        await cache.InsertEventLogsAsync(docs);
        cache.TotalCount.Should().Be(2);

        var size = cache.GetCacheFileSizeBytes();
        size.Should().BeGreaterThan(0);

        var streamed = new List<EventLogDoc>();
        await foreach (var item in cache.StreamAllEventLogsAsync())
        {
            streamed.Add(item);
        }

        // Assert
        streamed.Should().HaveCount(2);
        streamed[0].Id.Should().Be("el_1");
        streamed[0].User.Should().Be("Администратор");
        streamed[0].Event.Should().Be("_$Session$_.Start");
        streamed[0].Importance.Should().Be("Information");
        streamed[1].Id.Should().Be("el_2");
        streamed[1].User.Should().Be("Бухгалтер");

        // Cleanup on dispose
        cache.Dispose();
        File.Exists(dbPath).Should().BeFalse();
    }

    [Fact]
    public async Task TechLogCache_InsertAndStream_ShouldPreserveAllFieldsAndCleanupOnDispose()
    {
        // Arrange
        var cache = SessionCacheService.CreateTechLogCache(_testTempDir);
        var dbPath = cache.DbFilePath;
        File.Exists(dbPath).Should().BeTrue();

        var docs = new List<TechLogDoc>
        {
            new()
            {
                Id = "tl_1",
                Date = new DateTime(2026, 9, 4, 11, 30, 0, 500, DateTimeKind.Utc),
                DateFormatted = "2026-09-04 11:30:00.500000",
                Duration = 250_000,
                DurationMs = 25.0,
                DurationSec = 0.025,
                DurationFormatted = "25.00 мс",
                Event = "DBMSSQL",
                Level = 0,
                ProcessName = "rphost",
                ProcessId = "1234",
                User = "USR_1",
                Sql = "SELECT * FROM _Reference12 WHERE _ID = 100"
            }
        };

        // Act
        await cache.InsertTechLogsAsync(docs);
        cache.TotalCount.Should().Be(1);

        var size = cache.GetCacheFileSizeBytes();
        size.Should().BeGreaterThan(0);

        var streamed = new List<TechLogDoc>();
        await foreach (var item in cache.StreamAllTechLogsAsync())
        {
            streamed.Add(item);
        }

        // Assert
        streamed.Should().HaveCount(1);
        streamed[0].Id.Should().Be("tl_1");
        streamed[0].Event.Should().Be("DBMSSQL");
        streamed[0].Duration.Should().Be(250_000);
        streamed[0].Sql.Should().Contain("_Reference12");

        // Cleanup on dispose
        cache.Dispose();
        File.Exists(dbPath).Should().BeFalse();
    }

    [Fact]
    public void CleanupAllOrphanedTempFiles_ShouldRemoveOrphanedDatabases()
    {
        // Arrange
        var orphan1 = Path.Combine(_testTempDir, "session_lg_orphaned_1.db");
        var orphan2 = Path.Combine(_testTempDir, "session_tg_orphaned_2.db-wal");
        var regularFile = Path.Combine(_testTempDir, "keep_this.txt");

        File.WriteAllText(orphan1, "junk");
        File.WriteAllText(orphan2, "junk");
        File.WriteAllText(regularFile, "important");

        // Act
        SessionCacheService.CleanupAllOrphanedTempFiles(_testTempDir);

        // Assert
        File.Exists(orphan1).Should().BeFalse();
        File.Exists(orphan2).Should().BeFalse();
        File.Exists(regularFile).Should().BeTrue();
    }
}
