using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using OneSLogExporter.Core.Serialization;
using OneSLogExporter.Core.Services;
using OneSLogExporter.Core.State;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class JsonLogTransporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _evDumpDir;
    private readonly string _tgDumpDir;
    private readonly string _stateFile;

    public JsonLogTransporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"transporter_test_{Guid.NewGuid():N}");
        _evDumpDir = Path.Combine(_tempDir, "evdump");
        _tgDumpDir = Path.Combine(_tempDir, "tgdump");
        _stateFile = Path.Combine(_tempDir, "state.json");

        Directory.CreateDirectory(_evDumpDir);
        Directory.CreateDirectory(_tgDumpDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task ParseJsonDumpAsync_EventLog_ShouldReadDocumentsCorrectly()
    {
        // Arrange
        var filePath = Path.Combine(_evDumpDir, "data_evlog_1.json");
        var doc1 = new EventLogDoc
        {
            Id = "doc1",
            Date = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
            DateFormatted = "2026-09-13 10:00:00",
            Event = "_$Session$.Start",
            User = "Admin"
        };
        var doc2 = new EventLogDoc
        {
            Id = "doc2",
            Date = new DateTime(2026, 9, 13, 10, 0, 5, DateTimeKind.Utc),
            DateFormatted = "2026-09-13 10:00:05",
            Event = "_$Data$.Post",
            User = "User1"
        };

        var line1 = JsonSerializer.Serialize(doc1, LogJsonContext.Compact.EventLogDoc);
        var line2 = JsonSerializer.Serialize(doc2, LogJsonContext.Compact.EventLogDoc);
        await File.WriteAllLinesAsync(filePath, [line1, line2]);

        // Act
        var docs = new List<EventLogDoc>();
        await foreach (var doc in EventLogParser.ParseJsonDumpAsync(filePath))
        {
            docs.Add(doc);
        }

        // Assert
        docs.Should().HaveCount(2);
        docs[0].Id.Should().Be("doc1");
        docs[0].User.Should().Be("Admin");
        docs[1].Id.Should().Be("doc2");
        docs[1].User.Should().Be("User1");
    }

    [Fact]
    public async Task ParseJsonDumpAsync_TechLog_ShouldReadDocumentsCorrectly()
    {
        // Arrange
        var filePath = Path.Combine(_tgDumpDir, "data_tglog_1.json");
        var doc1 = new TechLogDoc
        {
            Id = "doc1",
            Date = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc),
            DateFormatted = "2026-09-13 10:00:00.000",
            Duration = 500,
            DurationMs = 0.5,
            DurationSec = 0.0005,
            DurationFormatted = "0.5 ms",
            Event = "DBMSSQL",
            Level = 0,
            ProcessName = "rphost",
            ProcessId = "1234",
            Sql = "SELECT 1"
        };

        var line1 = JsonSerializer.Serialize(doc1, LogJsonContext.Compact.TechLogDoc);
        await File.WriteAllLinesAsync(filePath, [line1]);

        // Act
        var docs = new List<TechLogDoc>();
        await foreach (var doc in TechLogParser.ParseJsonDumpAsync(filePath))
        {
            docs.Add(doc);
        }

        // Assert
        docs.Should().HaveCount(1);
        docs[0].Id.Should().Be("doc1");
        docs[0].ProcessName.Should().Be("rphost");
        docs[0].Sql.Should().Be("SELECT 1");
    }
}