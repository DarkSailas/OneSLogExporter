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

    [Fact]
    public async Task JsonLogTransporter_WhenFileTruncatedOrRotated_ShouldResetPositionAndReadNewContent()
    {
        // Arrange
        var stateTracker = new StateTracker(_stateFile);
        var options = new ExporterOptions
        {
            FileDump = new FileDumpSettings
            {
                EventLogDirectoryPath = _evDumpDir,
                EventLogEnabled = true
            },
            ClickHouse = new ClickHouseSettings
            {
                EventLogEnabled = true,
                ServerUrl = "http://localhost:8123"
            },
            Elastic = new ElasticSettings
            {
                EventLogEnabled = false
            }
        };

        var filePath = Path.Combine(_evDumpDir, "data_evlog_1.json");
        
        // 1. First iteration: write 3 large docs
        var initialDocs = new List<EventLogDoc>
        {
            new() { Id = "old1", Event = "OldEv1", Date = DateTime.UtcNow, DateFormatted = "2026-09-14 08:00:00" },
            new() { Id = "old2", Event = "OldEv2", Date = DateTime.UtcNow, DateFormatted = "2026-09-14 08:00:01" },
            new() { Id = "old3", Event = "OldEv3", Date = DateTime.UtcNow, DateFormatted = "2026-09-14 08:00:02" }
        };
        var initialLines = initialDocs.Select(d => JsonSerializer.Serialize(d, LogJsonContext.Compact.EventLogDoc)).ToList();
        await File.WriteAllLinesAsync(filePath, initialLines);
        var initialLength = new FileInfo(filePath).Length;

        // Simulate that previous run tracked the entire file
        stateTracker.MarkFilePosition(filePath, initialLength, initialLength);
        await stateTracker.SaveAsync();

        // 2. File rotation happens: file is rewritten with 1 new doc (smaller size)
        var newDoc = new EventLogDoc { Id = "new1", Event = "NewEv1", Date = DateTime.UtcNow, DateFormatted = "2026-09-14 09:00:00" };
        var newJson = JsonSerializer.Serialize(newDoc, LogJsonContext.Compact.EventLogDoc);
        await File.WriteAllLinesAsync(filePath, [newJson]);
        var newLength = new FileInfo(filePath).Length;

        newLength.Should().BeLessThan(initialLength);

        // 3. Act: Transporter runs with Mock Http for ClickHouse
        var mockHttp = new HttpClient(new MockSuccessHttpHandler());
        var transporter = new JsonLogTransporter(
            new ElasticPublisher(options.Elastic, NullLogger<ElasticPublisher>.Instance),
            new ClickHousePublisher(options.ClickHouse, NullLogger<ClickHousePublisher>.Instance, mockHttp),
            stateTracker,
            options,
            NullLogger<JsonLogTransporter>.Instance);

        await transporter.TransportEventLogsAsync();

        // 4. Assert: StateTracker must have adapted to the new length instead of being stuck at initialLength
        var updatedPos = stateTracker.GetLastPosition(filePath);
        updatedPos.Should().Be(newLength);
    }

    private sealed class MockSuccessHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}