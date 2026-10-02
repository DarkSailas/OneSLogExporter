using System.Text.Json;
using FluentAssertions;
using OneSLogExporter.Core.Serialization;
using Xunit;

namespace OneSLogExporter.Tests;

public sealed class ConfigSanitizerTests
{
    [Fact]
    public void SanitizeJson_ShouldNormalizeWindowsBackslashesInPathSettings()
    {
        var rawJson = """
        {
          "Exporter": {
            "FileDump": {
              "TechLogDirectoryPath": "C:\export\techlog",
              "EventLogDirectoryPath": "C:\export\eventlog"
            },
            "EventLog": {
              "DirectoryPath": "C:\1C_Server\reg_1541"
            },
            "TechLog": {
              "DirectoryPath": "C:\Logs"
            }
          }
        }
        """;

        var sanitized = ConfigSanitizer.SanitizeJson(rawJson);

        sanitized.Should().Contain("\"TechLogDirectoryPath\": \"C:/export/techlog\"");
        sanitized.Should().Contain("\"EventLogDirectoryPath\": \"C:/export/eventlog\"");
        sanitized.Should().Contain("\"DirectoryPath\": \"C:/1C_Server/reg_1541\"");
        sanitized.Should().Contain("\"DirectoryPath\": \"C:/Logs\"");

        // Проверяем, что результат валидный JSON для стандартного парсера .NET
        var act = () => JsonDocument.Parse(sanitized);
        act.Should().NotThrow();
    }

    [Fact]
    public void SanitizeJson_ShouldPreserveLineCommentsAndDoubleBackslashes()
    {
        var rawJson = """
        {
          "Exporter": {
            "FileDump": {
              "TechLogDirectoryPath": "C:\\Logs\\techlog", // Важный комментарий: путь C:\test
              "RetainedFileCountLimit": 30
            }
          }
        }
        """;

        var sanitized = ConfigSanitizer.SanitizeJson(rawJson);

        sanitized.Should().Contain("\"TechLogDirectoryPath\": \"C:/Logs/techlog\"");
        sanitized.Should().Contain(@"// Важный комментарий: путь C:\test");

        var act = () => JsonDocument.Parse(sanitized, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        act.Should().NotThrow();
    }

    [Fact]
    public void SanitizeConfigFile_ShouldAutoFixFileOnDisk()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_appsettings_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempFile, """
            {
              "Exporter": {
                "EventLog": {
                  "DirectoryPath": "C:\Logs\1C\reg_1541"
                }
              }
            }
            """);

            var changed = ConfigSanitizer.SanitizeConfigFile(tempFile);
            changed.Should().BeTrue();

            var contentAfter = File.ReadAllText(tempFile);
            contentAfter.Should().Contain("\"DirectoryPath\": \"C:/Logs/1C/reg_1541\"");

            var act = () => JsonDocument.Parse(contentAfter);
            act.Should().NotThrow();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
