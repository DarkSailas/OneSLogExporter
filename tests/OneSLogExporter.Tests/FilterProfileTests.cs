using System.Text.Json;
using FluentAssertions;
using OneSLogExporter.Core.Models;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class FilterProfileTests
{
    [Fact]
    public void FilterProfile_SerializationRoundTrip_ShouldPreserveAllProperties()
    {
        var profile = new FilterProfile
        {
            Version = "1.2.0",
            Title = "Профиль отладки блокировок",
            Description = "Фильтрует TLOCK и TDEADLOCK за последние 3 дня",
            ExportedAt = new DateTime(2026, 8, 30, 18, 0, 0, DateTimeKind.Local),
            TechLog = new TechLogFilterProfile
            {
                LogPath = @"C:\Logs\TechLog\rphost_1234",
                SearchText = "!DefUser -ragent",
                TimeFrom = "09:00:00",
                TimeTo = "18:00:00",
                DateFrom = new DateTime(2026, 8, 27),
                DateTo = new DateTime(2026, 8, 30),
                EventFilterIndex = 2,
                IncludeRunning = true,
                IncludeCompleted = false,
                MinDurationIndex = 1,
                SortPresetIndex = 2,
                LimitIndex = 5,
                ExcludeRphost = false,
                ExcludeRmngr = true,
                ExcludeRagent = true,
                ExcludeCompleted = true,
                ExcludeRunning = false,
                ExcludeEvents = ["CALL", "VCLIENT"],
                IncludedUsers = ["Администратор", "ГлавныйБухгалтер"],
                ExcludedUsers = ["Robot"],
                IncludedApps = ["1Cv8C"],
                ExcludedApps = ["BackgroundJob"]
            },
            EventLog = new EventLogFilterProfile
            {
                LogPath = @"C:\Logs\EventLog\1Cv8.lgd",
                SearchText = "Ошибка транзакции",
                ImportanceIndex = 1,
                IncludeError = true,
                IncludeWarn = true,
                ExcludeEvents = ["_$Session$_.Start"],
                IncludedUsers = ["Администратор"]
            },
            Settings = new AppSettingsProfile
            {
                TechLogPath = @"C:\Logs\1C",
                EventLogPath = @"C:\Logs\LGLogs",
                ElasticUrl = "http://elastic.example.com:9200",
                ElasticUser = "elastic",
                ElasticEnabled = true,
                TechLogIndexPrefix = "techlog-prod"
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(profile, options);

        json.Should().NotBeNullOrWhiteSpace();

        var deserialized = JsonSerializer.Deserialize<FilterProfile>(json);
        deserialized.Should().NotBeNull();
        deserialized!.Version.Should().Be("1.2.0");
        deserialized.Title.Should().Be("Профиль отладки блокировок");
        deserialized.TechLog.Should().NotBeNull();
        deserialized.TechLog!.LogPath.Should().Be(@"C:\Logs\TechLog\rphost_1234");
        deserialized.TechLog.SearchText.Should().Be("!DefUser -ragent");
        deserialized.TechLog.DateFrom.Should().Be(new DateTime(2026, 8, 27));
        deserialized.TechLog.DateTo.Should().Be(new DateTime(2026, 8, 30));
        deserialized.TechLog.IncludedUsers.Should().Contain("ГлавныйБухгалтер");
        deserialized.TechLog.ExcludeRmngr.Should().BeTrue();
        deserialized.EventLog.Should().NotBeNull();
        deserialized.EventLog!.LogPath.Should().Be(@"C:\Logs\EventLog\1Cv8.lgd");
        deserialized.EventLog.IncludeError.Should().BeTrue();
        deserialized.Settings.Should().NotBeNull();
        deserialized.Settings!.ElasticUrl.Should().Be("http://elastic.example.com:9200");
    }

    [Fact]
    public void FilterProfile_EmptyJson_ShouldDeserializeGracefully()
    {
        var json = "{}";
        var deserialized = JsonSerializer.Deserialize<FilterProfile>(json);

        deserialized.Should().NotBeNull();
        deserialized!.TechLog.Should().BeNull();
        deserialized.EventLog.Should().BeNull();
        deserialized.Settings.Should().BeNull();
    }
}
