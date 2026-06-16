using System.Text.Json;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

/// <summary>
/// Tests for ClaudeConfigService — ParseMemoryEntry, LoadInstalledPluginsWithMeta, etc.
/// </summary>
public class ClaudeConfigServiceTests
{
    // ── LoadInstalledPluginsWithMeta JSON format handling ──────────

    public class InstalledPluginsFormatTests
    {
        [Fact]
        public void ObjectFormat_WithObjectValues_ParsesCorrectly()
        {
            // Arrange — Widget-style Object format
            var json = """
                {
                  "version": 2,
                  "plugins": {
                    "my-plugin@source": {
                      "installedAt": "2026-04-01T10:00:00Z",
                      "installedVia": "Installed via Claude Sessions Sidekick"
                    }
                  }
                }
                """;

            // Act
            using var doc = JsonDocument.Parse(json);
            var result = ParsePluginsFromDoc(doc);

            // Assert
            Assert.Single(result);
            Assert.Equal("Installed via Claude Sessions Sidekick", result["my-plugin@source"]);
        }

        [Fact]
        public void ObjectFormat_WithArrayValues_ParsesAsDesktop()
        {
            // Arrange — Claude Desktop stores plugin values as Array
            var json = """
                {
                  "version": 2,
                  "plugins": {
                    "plugin-a@official": [{"scope": "project", "installPath": "/some/path"}],
                    "plugin-b@official": {"installedVia": "widget"}
                  }
                }
                """;

            // Act
            using var doc = JsonDocument.Parse(json);
            var result = ParsePluginsFromDoc(doc);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal("Claude Desktop", result["plugin-a@official"]);
            Assert.Equal("widget", result["plugin-b@official"]);
        }

        [Fact]
        public void RootArrayFormat_ParsesPluginIds()
        {
            // Arrange — Root-level Array format (some Claude Desktop versions)
            var json = """
                [
                  {"id": "plugin-x@source", "scope": "project"},
                  {"id": "plugin-y@source", "scope": "global"}
                ]
                """;

            // Act
            using var doc = JsonDocument.Parse(json);
            var result = ParsePluginsFromDoc(doc);

            // Assert
            Assert.Equal(2, result.Count);
            Assert.Equal("Claude Desktop", result["plugin-x@source"]);
            Assert.Equal("Claude Desktop", result["plugin-y@source"]);
        }

        [Fact]
        public void RootArrayFormat_SkipsItemsWithoutId()
        {
            // Arrange
            var json = """[{"scope": "project"}, {"id": "valid@source"}]""";

            // Act
            using var doc = JsonDocument.Parse(json);
            var result = ParsePluginsFromDoc(doc);

            // Assert
            Assert.Single(result);
            Assert.True(result.ContainsKey("valid@source"));
        }

        [Fact]
        public void EmptyObject_ReturnsEmpty()
        {
            // Arrange
            var json = """{"version": 2, "plugins": {}}""";

            // Act
            using var doc = JsonDocument.Parse(json);
            var result = ParsePluginsFromDoc(doc);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void EmptyArray_ReturnsEmpty()
        {
            // Arrange
            var json = "[]";

            // Act
            using var doc = JsonDocument.Parse(json);
            var result = ParsePluginsFromDoc(doc);

            // Assert
            Assert.Empty(result);
        }

        /// <summary>
        /// Extracts the JSON parsing logic from LoadInstalledPluginsWithMeta
        /// so we can test it without touching the filesystem.
        /// </summary>
        private static Dictionary<string, string> ParsePluginsFromDoc(JsonDocument doc)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("id", out var idProp))
                    {
                        var id = idProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(id))
                        {
                            result[id] = "Claude Desktop";
                        }
                    }
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("plugins", out var plugins) &&
                     plugins.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in plugins.EnumerateObject())
                {
                    var via = "";
                    if (prop.Value.ValueKind == JsonValueKind.Object &&
                        prop.Value.TryGetProperty("installedVia", out var v))
                    {
                        via = v.GetString() ?? "";
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        via = "Claude Desktop";
                    }
                    result[prop.Name] = via;
                }
            }

            return result;
        }
    }

    // ── ParseMemoryEntry ───────────────────────────────────────────

    public class ParseMemoryEntryTests
    {
        [Fact]
        public void Parse_FullFrontmatter_ExtractsAllFields()
        {
            // Arrange
            var raw = """
                ---
                name: Test Memory
                description: A test memory entry
                type: feedback
                ---

                This is the body content.
                It has multiple lines.
                """;

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("Test Memory", entry.Name);
            Assert.Equal("A test memory entry", entry.Description);
            Assert.Equal("feedback", entry.Type);
            Assert.Contains("body content", entry.Body);
            Assert.Contains("multiple lines", entry.Body);
        }

        [Fact]
        public void Parse_WithToolsField_ExtractsTools()
        {
            // Arrange
            var raw = """
                ---
                name: My Agent
                description: Does things
                type: agent
                tools: Read, Write, Bash
                ---

                Agent instructions here.
                """;

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("My Agent", entry.Name);
            Assert.Equal("Read, Write, Bash", entry.Tools);
        }

        [Fact]
        public void Parse_NoFrontmatter_WholeContentIsBody()
        {
            // Arrange
            var raw = "Just plain text without any frontmatter markers.";

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert - MemoryEntry fields default to "" not null
            Assert.Equal("", entry.Name);
            Assert.Equal("", entry.Type);
            Assert.Equal("Just plain text without any frontmatter markers.", entry.Body);
        }

        [Fact]
        public void Parse_EmptyBody_Works()
        {
            // Arrange
            var raw = """
                ---
                name: Empty Body Entry
                type: user
                ---
                """;

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("Empty Body Entry", entry.Name);
            Assert.Equal("user", entry.Type);
            Assert.True(string.IsNullOrWhiteSpace(entry.Body));
        }

        [Fact]
        public void Parse_OnlyNameInFrontmatter_OtherFieldsRemainDefault()
        {
            // Arrange
            var raw = """
                ---
                name: Just A Name
                ---

                Some content.
                """;

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert - MemoryEntry fields default to "" not null
            Assert.Equal("Just A Name", entry.Name);
            Assert.Equal("", entry.Description);
            Assert.Equal("", entry.Type);
            Assert.Equal("", entry.Tools);
        }

        [Fact]
        public void Parse_FrontmatterWithExtraWhitespace_Handles()
        {
            // Arrange
            var raw = "---\n  name:   Spaced Out  \n  type:   project  \n---\nBody.";

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("Spaced Out", entry.Name);
            Assert.Equal("project", entry.Type);
        }

        [Fact]
        public void Parse_ColonInValue_KeepsFullValue()
        {
            // Arrange - description contains colons
            var raw = """
                ---
                name: Test
                description: URL: https://example.com/api
                type: reference
                ---

                Body.
                """;

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("URL: https://example.com/api", entry.Description);
        }

        [Fact]
        public void Parse_UnknownKeys_AreIgnored()
        {
            // Arrange
            var raw = """
                ---
                name: Test
                unknown_key: some value
                type: feedback
                another_field: xyz
                ---

                Body.
                """;

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("Test", entry.Name);
            Assert.Equal("feedback", entry.Type);
        }

        [Fact]
        public void Parse_CaseInsensitiveKeys()
        {
            // Arrange
            var raw = """
                ---
                Name: Test Entry
                TYPE: user
                Description: A description
                ---

                Body.
                """;

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("Test Entry", entry.Name);
            Assert.Equal("user", entry.Type);
            Assert.Equal("A description", entry.Description);
        }

        [Fact]
        public void Parse_WindowsLineEndings_Works()
        {
            // Arrange
            var raw = "---\r\nname: Windows\r\ntype: feedback\r\n---\r\nBody with CRLF.";

            // Act
            var entry = ClaudeConfigService.ParseMemoryEntry(raw);

            // Assert
            Assert.Equal("Windows", entry.Name);
            Assert.Equal("feedback", entry.Type);
            Assert.Contains("Body with CRLF", entry.Body);
        }
    }

    // ── IsReducedContextModel ──────────────────────────────────────
    // Locks in the alias → context-window mapping after the Opus 4.8 launch:
    // opus / sonnet aliases now resolve to 1M models, only haiku still 200k.

    public class IsReducedContextModelTests
    {
        [Theory]
        [InlineData("haiku")] // Haiku 4.5 — still 200k
        [InlineData("claude-sonnet-4-5")] // legacy 200k by explicit ID
        [InlineData("claude-sonnet-4-5-20250929")]
        [InlineData("claude-3-5-sonnet-20241022")]
        [InlineData("claude-3-opus-20240229")]
        [InlineData("claude-3-haiku-20240307")]
        public void Reduced_ReturnsTrue(string model)
        {
            Assert.True(ClaudeConfigService.IsReducedContextModel(model));
        }

        [Theory]
        [InlineData("opus")]   // alias → Opus 4.8 = 1M
        [InlineData("sonnet")] // alias → Sonnet 4.6 = 1M
        [InlineData("claude-opus-4-8")]
        [InlineData("claude-opus-4-7")]
        [InlineData("claude-opus-4-6")]
        [InlineData("claude-sonnet-4-6")]
        [InlineData("claude-fable-5")]
        [InlineData(null)]
        [InlineData("")]
        public void NotReduced_ReturnsFalse(string? model)
        {
            Assert.False(ClaudeConfigService.IsReducedContextModel(model));
        }
    }
}
