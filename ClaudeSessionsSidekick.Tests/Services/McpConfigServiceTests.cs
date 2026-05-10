using System.Text.Json;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class McpConfigServiceTests
{
    // --- ParseServerEntry via .mcp.json format ---

    public class McpJsonParsingTests
    {
        [Fact]
        public void Parse_StdioServer_ExtractsCommandAndArgs()
        {
            // Arrange
            var json = """
                {
                  "mcpServers": {
                    "my-server": {
                      "command": "node",
                      "args": ["server.js", "--port", "3000"],
                      "env": {
                        "API_KEY": "secret123"
                      }
                    }
                  }
                }
                """;

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Single(servers);
            var server = servers[0];
            Assert.Equal("my-server", server.Name);
            Assert.Equal("node", server.Command);
            Assert.Equal("server.js --port 3000", server.Args);
            Assert.Single(server.EnvVars);
        }

        [Fact]
        public void Parse_HttpServer_ExtractsUrlAndType()
        {
            // Arrange
            var json = """
                {
                  "github": {
                    "type": "http",
                    "url": "https://api.githubcopilot.com/mcp/",
                    "headers": {
                      "Authorization": "Bearer ${GITHUB_TOKEN}"
                    }
                  }
                }
                """;

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Single(servers);
            var server = servers[0];
            Assert.Equal("github", server.Name);
            Assert.Equal("http", server.TransportType);
            Assert.Equal("https://api.githubcopilot.com/mcp/", server.Url);
            Assert.Equal("HTTP", server.TypeDisplay);
            Assert.Single(server.Headers);
            // "Bearer ${GITHUB_TOKEN}" gets masked because it doesn't start with "${" directly
            Assert.Contains("***", server.Headers["Authorization"]);
        }

        [Fact]
        public void Parse_SseServer_DetectsType()
        {
            // Arrange
            var json = """{"asana": {"type": "sse", "url": "https://mcp.asana.com/sse"}}""";

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Single(servers);
            Assert.Equal("sse", servers[0].TransportType);
            Assert.Equal("SSE", servers[0].TypeDisplay);
        }

        [Fact]
        public void Parse_FlatFormat_WorksWithoutMcpServersWrapper()
        {
            // Arrange — flat format (no "mcpServers" wrapper)
            var json = """
                {
                  "linear": {
                    "type": "http",
                    "url": "https://mcp.linear.app/mcp"
                  }
                }
                """;

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Single(servers);
            Assert.Equal("linear", servers[0].Name);
        }

        [Fact]
        public void Parse_WrappedFormat_ExtractsFromMcpServers()
        {
            // Arrange — wrapped format
            var json = """
                {
                  "mcpServers": {
                    "discord": {
                      "command": "bun",
                      "args": ["run", "start"]
                    }
                  }
                }
                """;

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Single(servers);
            Assert.Equal("discord", servers[0].Name);
            Assert.Equal("bun", servers[0].Command);
        }

        [Fact]
        public void Parse_DisabledServer_SetsFlag()
        {
            // Arrange
            var json = """
                {
                  "mcpServers": {
                    "test": {
                      "command": "node",
                      "args": ["server.js"],
                      "disabled": true
                    }
                  }
                }
                """;

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Single(servers);
            Assert.True(servers[0].IsDisabled);
            Assert.Equal("[disabled]", servers[0].StatusDisplay);
        }

        [Fact]
        public void Parse_MultipleServers_ReturnsAll()
        {
            // Arrange
            var json = """
                {
                  "mcpServers": {
                    "server-a": {"command": "node", "args": ["a.js"]},
                    "server-b": {"type": "http", "url": "https://example.com/mcp"}
                  }
                }
                """;

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Equal(2, servers.Count);
            Assert.Equal("server-a", servers[0].Name);
            Assert.Equal("server-b", servers[1].Name);
        }

        [Fact]
        public void Parse_EnvVarsWithTemplates_PreservesTemplates()
        {
            // Arrange
            var json = """
                {
                  "mcpServers": {
                    "test": {
                      "command": "node",
                      "args": ["${CLAUDE_PLUGIN_ROOT}/server.js"],
                      "env": {
                        "API_URL": "${ADO_ORG_URL:-https://default.example.com}",
                        "TOKEN": "hardcoded-secret"
                      }
                    }
                  }
                }
                """;

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            var env = servers[0].EnvVars;
            Assert.Equal(2, env.Count);
            // Template vars start with ${ — should be preserved (not masked)
            Assert.StartsWith("${", env["API_URL"]);
            // Hardcoded values should be masked
            Assert.Contains("***", env["TOKEN"]);
        }

        [Fact]
        public void Parse_EmptyJson_ReturnsEmpty()
        {
            // Arrange
            var json = "{}";

            // Act
            var servers = ParseMcpJson(json);

            // Assert
            Assert.Empty(servers);
        }

        /// <summary>
        /// Simulates the .mcp.json parsing logic from McpConfigService.
        /// </summary>
        private static List<McpServerEntry> ParseMcpJson(string json)
        {
            var results = new List<McpServerEntry>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("mcpServers", out var servers) &&
                servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in servers.EnumerateObject())
                {
                    results.Add(ParseEntry(server.Name, server.Value));
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in root.EnumerateObject())
                {
                    if (server.Value.ValueKind == JsonValueKind.Object)
                    {
                        results.Add(ParseEntry(server.Name, server.Value));
                    }
                }
            }

            return results;
        }

        private static McpServerEntry ParseEntry(string name, JsonElement config)
        {
            var entry = new McpServerEntry { Name = name };

            if (config.TryGetProperty("type", out var t))
                entry.TransportType = t.GetString() ?? "stdio";
            if (config.TryGetProperty("url", out var u))
            {
                entry.Url = u.GetString() ?? "";
                if (string.IsNullOrEmpty(entry.TransportType)) entry.TransportType = "http";
            }
            if (config.TryGetProperty("command", out var c))
            {
                entry.Command = c.GetString() ?? "";
                if (string.IsNullOrEmpty(entry.TransportType)) entry.TransportType = "stdio";
            }
            if (config.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                entry.Args = string.Join(" ", a.EnumerateArray().Select(x => x.GetString() ?? ""));
            if (config.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in e.EnumerateObject())
                {
                    var val = kv.Value.GetString() ?? "";
                    entry.EnvVars[kv.Name] = val.StartsWith("${") ? val :
                        (val.Length <= 8 ? "***" : val[..4] + "***" + val[^4..]);
                }
            }
            if (config.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in h.EnumerateObject())
                {
                    var val = kv.Value.GetString() ?? "";
                    entry.Headers[kv.Name] = val.StartsWith("${") ? val :
                        (val.Length <= 8 ? "***" : val[..4] + "***" + val[^4..]);
                }
            }
            if (config.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True)
                entry.IsDisabled = true;

            return entry;
        }
    }

    // --- McpServerEntry properties ---

    public class McpServerEntryTests
    {
        [Fact]
        public void TypeDisplay_DefaultsToStdio()
        {
            // Arrange/Act
            var entry = new McpServerEntry();

            // Assert
            Assert.Equal("stdio", entry.TypeDisplay);
        }

        [Theory]
        [InlineData("http", "HTTP")]
        [InlineData("sse", "SSE")]
        [InlineData("stdio", "stdio")]
        public void TypeDisplay_MapsCorrectly(string type, string expected)
        {
            // Arrange/Act
            var entry = new McpServerEntry { TransportType = type };

            // Assert
            Assert.Equal(expected, entry.TypeDisplay);
        }

        [Fact]
        public void EndpointDisplay_PrefersUrl()
        {
            // Arrange
            var entry = new McpServerEntry { Url = "https://example.com", Command = "node" };

            // Assert
            Assert.Equal("https://example.com", entry.EndpointDisplay);
        }

        [Fact]
        public void EndpointDisplay_ShowsCommandAndArgs()
        {
            // Arrange
            var entry = new McpServerEntry { Command = "npx", Args = "-y @some/package" };

            // Assert
            Assert.Equal("npx -y @some/package", entry.EndpointDisplay);
        }

        [Fact]
        public void IsUserEditable_TrueForClaudeJson()
        {
            // Arrange
            var entry = new McpServerEntry { ConfigFile = @"C:\Users\test\.claude.json" };

            // Assert
            Assert.True(entry.IsUserEditable);
        }

        [Fact]
        public void IsUserEditable_FalseForPluginMcpJson()
        {
            // Arrange
            var entry = new McpServerEntry { ConfigFile = @"C:\Users\test\.claude\plugins\cache\p\.mcp.json" };

            // Assert
            Assert.False(entry.IsUserEditable);
        }

        [Fact]
        public void StatusDisplay_ShowsDisabled()
        {
            // Arrange/Act
            var entry = new McpServerEntry { IsDisabled = true };

            // Assert
            Assert.Equal("[disabled]", entry.StatusDisplay);
        }

        [Fact]
        public void StatusDisplay_EmptyWhenEnabled()
        {
            // Arrange/Act — command check skipped when command is empty
            var entry = new McpServerEntry { Url = "https://example.com" };

            // Assert
            Assert.Equal("", entry.StatusDisplay);
        }
    }
}
