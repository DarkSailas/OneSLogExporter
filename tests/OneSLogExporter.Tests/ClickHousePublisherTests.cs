using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Services;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class ClickHousePublisherTests
{
    private sealed class MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task TestConnectionAsync_WhenServerReturnsOk_ShouldSucceedWithVersion()
    {
        var handler = new MockHttpHandler(req =>
        {
            req.Headers.Contains("X-ClickHouse-User").Should().BeTrue();
            req.Headers.GetValues("X-ClickHouse-User").First().Should().Be("test_user");
            req.Headers.Contains("X-ClickHouse-Key").Should().BeTrue();
            req.Headers.GetValues("X-ClickHouse-Key").First().Should().Be("secret_password");
            req.Headers.Authorization.Should().BeNull();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("24.8.3.15\n")
            };
        });

        using var client = new HttpClient(handler);
        var settings = new ClickHouseSettings
        {
            ServerUrl = "http://localhost:8123",
            User = "test_user",
            Password = "secret_password"
        };

        using var publisher = new ClickHousePublisher(settings, NullLogger<ClickHousePublisher>.Instance, client);

        var (success, message) = await publisher.TestConnectionAsync();

        success.Should().BeTrue();
        message.Should().Contain("24.8.3.15");
    }

    [Fact]
    public async Task BulkInsertTechLogAsync_ShouldSendBatchesAndReturnCount()
    {
        var requests = new List<HttpRequestMessage>();

        var handler = new MockHttpHandler(req =>
        {
            requests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("")
            };
        });

        using var client = new HttpClient(handler);
        var settings = new ClickHouseSettings
        {
            ServerUrl = "http://localhost:8123",
            Database = "prod_db",
            TechLogTable = "techlog_test",
            BulkBatchSize = 10
        };

        using var publisher = new ClickHousePublisher(settings, NullLogger<ClickHousePublisher>.Instance, client);

        var docs = new List<TechLogDoc>
        {
            new()
            {
                Id = "tl_1",
                Date = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc),
                DateFormatted = "2026-09-11 10:00:00",
                Duration = 1000,
                DurationMs = 1.0,
                DurationSec = 0.001,
                DurationFormatted = "1.00 ms",
                Event = "EXCP",
                Level = 0,
                ProcessName = "rphost_1234",
                Descr = "Test Exception"
            },
            new()
            {
                Id = "tl_2",
                Date = new DateTime(2026, 9, 11, 10, 0, 1, DateTimeKind.Utc),
                DateFormatted = "2026-09-11 10:00:01",
                Duration = 2000,
                DurationMs = 2.0,
                DurationSec = 0.002,
                DurationFormatted = "2.00 ms",
                Event = "CALL",
                Level = 1,
                ProcessName = "rphost_1234",
                Descr = "Test Call"
            }
        };

        var (success, failed) = await publisher.BulkInsertTechLogAsync(docs);

        success.Should().Be(2);
        failed.Should().Be(0);
        requests.Should().HaveCount(3); // CREATE DATABASE, CREATE TABLE, INSERT
    }

    [Fact]
    public async Task BulkInsertEventLogAsync_ShouldSendBatchesAndReturnCount()
    {
        var requests = new List<HttpRequestMessage>();

        var requestBodies = new List<string>();
        var handler = new MockHttpHandler(req =>
        {
            requests.Add(req);
            if (req.Content != null)
            {
                requestBodies.Add(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("")
            };
        });

        using var client = new HttpClient(handler);
        var settings = new ClickHouseSettings
        {
            ServerUrl = "http://localhost:8123",
            Database = "prod_db",
            EventLogTable = "eventlog_test",
            BulkBatchSize = 10
        };

        using var publisher = new ClickHousePublisher(settings, NullLogger<ClickHousePublisher>.Instance, client);

        var docs = new List<EventLogDoc>
        {
            new()
            {
                Id = "el_1",
                Date = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc),
                DateFormatted = "2026-09-11 10:00:00",
                Event = "_$Session$_.Start",
                User = "Администратор",
                Comment = "Сеанс начат"
            }
        };

        var (success, failed) = await publisher.BulkInsertEventLogAsync(docs);

        success.Should().Be(1);
        failed.Should().Be(0);
        requests.Should().HaveCount(3); // CREATE DATABASE, CREATE TABLE, INSERT

        requestBodies.Should().NotBeEmpty();
        var insertContent = requestBodies.Last();
        insertContent.Should().Contain("\"DateTime\":\"2026-09-11 10:00:00.000\"");
        insertContent.Should().Contain("\"Date\":\"2026-09-11 10:00:00.000\"");
        insertContent.Should().NotContain("Z\"");

        var lastReqQuery = Uri.UnescapeDataString(requests.Last().RequestUri!.Query);
        lastReqQuery.Should().Contain("input_format_null_as_default=1", "ClickHouse должен корректно вставлять дефолтные значения вместо падения на NULL");
    }

    [Theory]
    [InlineData("192.0.2.128", "http://192.0.2.128:8123")]
    [InlineData("192.0.2.128:8123", "http://192.0.2.128:8123")]
    [InlineData("http://192.0.2.128", "http://192.0.2.128:8123")]
    [InlineData("http://192.0.2.128:8123/", "http://192.0.2.128:8123")]
    [InlineData("http://localhost:8123", "http://localhost:8123")]
    [InlineData("", "http://localhost:8123")]
    [InlineData(null, "http://localhost:8123")]
    public void NormalizeServerUrl_ShouldFormatHostAndPortCorrectly(string? input, string expected)
    {
        var result = ClickHousePublisher.NormalizeServerUrl(input);
        result.Should().Be(expected);
    }
}
