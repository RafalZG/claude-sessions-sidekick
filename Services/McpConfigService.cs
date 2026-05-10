using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSessionsSidekick.Services;

public static class McpConfigService
{
    public static List<McpServerEntry> ScanAll()
    {
        var results = new List<McpServerEntry>();

        // 1. ~/.claude.json (primary MCP config file in user home)
        var userHome = Path.GetDirectoryName(ClaudeConfigService.ClaudeHomeDir)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeJson = Path.Combine(userHome, ".claude.json");
        AddFromSettingsFile(results, claudeJson, "User config (~/.claude.json)");

        // 2. Global ~/.claude/settings.json
        var globalSettings = Path.Combine(ClaudeConfigService.ClaudeHomeDir, "settings.json");
        AddFromSettingsFile(results, globalSettings, "Global settings");

        // 3. Plugin .mcp.json files (installed plugins only)
        ScanPluginMcpServers(results);

        // 4. Project-level settings (from Quick Launch entries)
        ScanProjectSettings(results);

        return results;
    }

    public static List<McpServerEntry> ScanForSource(string source)
    {
        return ScanAll().Where(s => s.Source == source).ToList();
    }

    public static List<string> GetSources()
    {
        var sources = new List<string>();

        var userHome = Path.GetDirectoryName(ClaudeConfigService.ClaudeHomeDir)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeJson = Path.Combine(userHome, ".claude.json");
        if (File.Exists(claudeJson))
        {
            sources.Add("User config (~/.claude.json)");
        }

        sources.Add("Global settings");
        sources.Add("Installed plugins");

        return sources;
    }

    private static void ScanPluginMcpServers(List<McpServerEntry> results)
    {
        var pluginsDir = Path.Combine(ClaudeConfigService.ClaudeHomeDir, "plugins");
        var installed = ClaudeConfigService.LoadInstalledPlugins();

        // Only show MCP servers from installed plugins (cache has the active versions)
        var cacheDir = Path.Combine(pluginsDir, "cache");
        if (Directory.Exists(cacheDir))
        {
            foreach (var sourceDir in Directory.GetDirectories(cacheDir))
            {
                var sourceName = Path.GetFileName(sourceDir);
                foreach (var pluginDir in Directory.GetDirectories(sourceDir))
                {
                    var pluginName = Path.GetFileName(pluginDir);

                    // Check if this plugin is installed
                    var pluginId = $"{pluginName}@{sourceName}";
                    if (!installed.Contains(pluginId))
                    {
                        continue;
                    }

                    // Pick latest version
                    var versionDirs = Directory.GetDirectories(pluginDir)
                        .OrderByDescending(d => Directory.GetLastWriteTime(d))
                        .ToArray();

                    var targetDir = versionDirs.Length > 0 ? versionDirs[0] : pluginDir;
                    var mcpFile = Path.Combine(targetDir, ".mcp.json");
                    if (File.Exists(mcpFile))
                    {
                        var version = versionDirs.Length > 0 ? Path.GetFileName(versionDirs[0]) : "";
                        var versionSuffix = !string.IsNullOrEmpty(version) ? $" v{version}" : "";
                        AddFromMcpJson(results, mcpFile, $"Plugin: {pluginName}{versionSuffix} ({sourceName})");
                    }
                }
            }
        }

        // Also scan marketplace plugins that are installed but might not be in cache yet
        var mktDir = Path.Combine(pluginsDir, "marketplaces");
        if (Directory.Exists(mktDir))
        {
            // Collect which plugin names we already found from cache
            var foundNames = new HashSet<string>(
                results.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var marketDir in Directory.GetDirectories(mktDir))
            {
                var mktName = Path.GetFileName(marketDir);
                var pluginSubDir = Path.Combine(marketDir, "plugins");
                var searchDir = Directory.Exists(pluginSubDir) ? pluginSubDir : marketDir;

                foreach (var pluginDir in Directory.GetDirectories(searchDir))
                {
                    var pluginName = Path.GetFileName(pluginDir);
                    if (pluginName.StartsWith('.'))
                    {
                        continue;
                    }

                    // Only if installed and not already found via cache
                    var pluginId = $"{pluginName}@{mktName}";
                    if (!installed.Contains(pluginId) || foundNames.Contains(pluginName))
                    {
                        continue;
                    }

                    var mcpFile = Path.Combine(pluginDir, ".mcp.json");
                    if (File.Exists(mcpFile))
                    {
                        AddFromMcpJson(results, mcpFile, $"Plugin: {pluginName} ({mktName})");
                    }
                }
            }
        }
    }

    private static void ScanProjectSettings(List<McpServerEntry> results)
    {
        var projectsRoot = ClaudeConfigService.ClaudeProjectsRoot;
        if (!Directory.Exists(projectsRoot))
        {
            return;
        }

        foreach (var projectDir in Directory.GetDirectories(projectsRoot))
        {
            var projectName = Path.GetFileName(projectDir);
            var settingsFile = Path.Combine(projectDir, "settings.json");
            var localSettingsFile = Path.Combine(projectDir, "settings.local.json");

            AddFromSettingsFile(results, settingsFile, $"Project: {projectName}");
            AddFromSettingsFile(results, localSettingsFile, $"Project (local): {projectName}");
        }
    }

    private static void AddFromSettingsFile(List<McpServerEntry> results, string filePath, string source)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var server in servers.EnumerateObject())
            {
                results.Add(ParseServerEntry(server.Name, server.Value, source, filePath));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to parse MCP config from {filePath}: {ex.Message}");
        }
    }

    private static void AddFromMcpJson(List<McpServerEntry> results, string filePath, string source)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Two formats:
            // 1. {"mcpServers": {"name": {...}}}
            // 2. {"name": {...}} (flat, each top-level key is a server)
            if (root.TryGetProperty("mcpServers", out var servers) &&
                servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in servers.EnumerateObject())
                {
                    results.Add(ParseServerEntry(server.Name, server.Value, source, filePath));
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in root.EnumerateObject())
                {
                    results.Add(ParseServerEntry(server.Name, server.Value, source, filePath));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to parse .mcp.json from {filePath}: {ex.Message}");
        }
    }

    private static McpServerEntry ParseServerEntry(string name, JsonElement config, string source, string filePath)
    {
        var entry = new McpServerEntry
        {
            Name = name,
            Source = source,
            ConfigFile = filePath
        };

        if (config.TryGetProperty("type", out var typeProp))
        {
            entry.TransportType = typeProp.GetString() ?? "stdio";
        }

        if (config.TryGetProperty("url", out var urlProp))
        {
            entry.Url = urlProp.GetString() ?? "";
            if (string.IsNullOrEmpty(entry.TransportType))
            {
                entry.TransportType = "http";
            }
        }

        if (config.TryGetProperty("command", out var cmdProp))
        {
            entry.Command = cmdProp.GetString() ?? "";
            if (string.IsNullOrEmpty(entry.TransportType))
            {
                entry.TransportType = "stdio";
            }
        }

        if (config.TryGetProperty("args", out var argsProp) && argsProp.ValueKind == JsonValueKind.Array)
        {
            entry.Args = string.Join(" ", argsProp.EnumerateArray().Select(a => a.GetString() ?? ""));
        }

        if (config.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var envVar in envProp.EnumerateObject())
            {
                var val = envVar.Value.GetString() ?? "";
                // Mask values that look like secrets
                var masked = val.StartsWith("${") ? val : MaskValue(val);
                entry.EnvVars[envVar.Name] = masked;
            }
        }

        if (config.TryGetProperty("headers", out var headersProp) && headersProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersProp.EnumerateObject())
            {
                var val = header.Value.GetString() ?? "";
                var masked = val.StartsWith("${") ? val : MaskValue(val);
                entry.Headers[header.Name] = masked;
            }
        }

        if (config.TryGetProperty("disabled", out var disabledProp) &&
            disabledProp.ValueKind == JsonValueKind.True)
        {
            entry.IsDisabled = true;
        }

        return entry;
    }

    // ---- CRUD for ~/.claude.json mcpServers ----

    private static string GetClaudeJsonPath()
    {
        var userHome = Path.GetDirectoryName(ClaudeConfigService.ClaudeHomeDir)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".claude.json");
    }

    public static void SaveServer(McpServerEntry server, string? originalName = null)
    {
        var path = GetClaudeJsonPath();
        var node = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
            : new JsonObject();

        var servers = node["mcpServers"] as JsonObject ?? new JsonObject();
        node["mcpServers"] = servers;

        // Guard: never save masked values back to disk
        foreach (var kv in server.EnvVars)
        {
            if (kv.Value.Contains("***"))
            {
                throw new InvalidOperationException(
                    $"Env var '{kv.Key}' contains masked value. Use LoadServerRaw() for editing.");
            }
        }

        // Remove old name if renaming
        if (originalName != null && originalName != server.Name)
        {
            servers.Remove(originalName);
        }

        var serverNode = new JsonObject();

        if (!string.IsNullOrEmpty(server.Url))
        {
            if (!string.IsNullOrEmpty(server.TransportType) && server.TransportType != "stdio")
            {
                serverNode["type"] = server.TransportType;
            }
            serverNode["url"] = server.Url;
        }
        else
        {
            serverNode["command"] = server.Command;
            if (!string.IsNullOrEmpty(server.Args))
            {
                var argsArray = new JsonArray();
                foreach (var arg in SplitArgs(server.Args))
                {
                    argsArray.Add(arg);
                }
                serverNode["args"] = argsArray;
            }
        }

        if (server.EnvVars.Count > 0)
        {
            var envNode = new JsonObject();
            foreach (var kv in server.EnvVars)
            {
                envNode[kv.Key] = kv.Value;
            }
            serverNode["env"] = envNode;
        }

        if (server.Headers.Count > 0)
        {
            var headersNode = new JsonObject();
            foreach (var kv in server.Headers)
            {
                headersNode[kv.Key] = kv.Value;
            }
            serverNode["headers"] = headersNode;
        }

        if (server.IsDisabled)
        {
            serverNode["disabled"] = true;
        }

        servers[server.Name] = serverNode;

        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        ClaudeConfigService.SaveText(path, json);
        AppLogger.Info($"MCP server '{server.Name}' saved to {path}");
    }

    public static void RemoveServer(string name)
    {
        var path = GetClaudeJsonPath();
        if (!File.Exists(path))
        {
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (node?["mcpServers"] is JsonObject servers)
        {
            servers.Remove(name);
            var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            ClaudeConfigService.SaveText(path, json);
            AppLogger.Info($"MCP server '{name}' removed from {path}");
        }
    }

    public static void SetServerDisabled(string name, bool disabled)
    {
        var path = GetClaudeJsonPath();
        if (!File.Exists(path))
        {
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (node?["mcpServers"] is JsonObject servers && servers[name] is JsonObject server)
        {
            if (disabled)
            {
                server["disabled"] = true;
            }
            else
            {
                server.Remove("disabled");
            }
            var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            ClaudeConfigService.SaveText(path, json);
            AppLogger.Info($"MCP server '{name}' {(disabled ? "disabled" : "enabled")}");
        }
    }

    /// <summary>Reads raw env vars (unmasked) for editing.</summary>
    public static McpServerEntry? LoadServerRaw(string name)
    {
        var path = GetClaudeJsonPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("mcpServers", out var servers) &&
                servers.TryGetProperty(name, out var config))
            {
                var entry = new McpServerEntry { Name = name, ConfigFile = path };
                if (config.TryGetProperty("type", out var t)) entry.TransportType = t.GetString() ?? "stdio";
                if (config.TryGetProperty("url", out var u)) { entry.Url = u.GetString() ?? ""; entry.TransportType ??= "http"; }
                if (config.TryGetProperty("command", out var c)) entry.Command = c.GetString() ?? "";
                if (config.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                {
                    entry.Args = string.Join(" ", a.EnumerateArray().Select(x => x.GetString() ?? ""));
                }
                if (config.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in e.EnumerateObject())
                    {
                        entry.EnvVars[kv.Name] = kv.Value.GetString() ?? "";
                    }
                }
                if (config.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in h.EnumerateObject())
                    {
                        entry.Headers[kv.Name] = kv.Value.GetString() ?? "";
                    }
                }
                if (config.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True)
                {
                    entry.IsDisabled = true;
                }
                return entry;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to load MCP server '{name}': {ex.Message}");
        }
        return null;
    }

    private static string[] SplitArgs(string args)
    {
        // Simple split — handles quoted args later if needed
        return args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string MaskValue(string value)
    {
        if (value.Length <= 16)
        {
            return "***";
        }
        return value[..4] + "***" + value[^4..];
    }
}

public class McpServerEntry
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string TransportType { get; set; } = "stdio";
    public string Command { get; set; } = "";
    public string Args { get; set; } = "";
    public string Url { get; set; } = "";
    public string ConfigFile { get; set; } = "";
    public bool IsDisabled { get; set; }
    public Dictionary<string, string> EnvVars { get; set; } = new();
    public Dictionary<string, string> Headers { get; set; } = new();

    public string TypeDisplay => TransportType switch
    {
        "http" => "HTTP",
        "sse" => "SSE",
        _ => "stdio"
    };

    public string StatusDisplay
    {
        get
        {
            if (IsDisabled) return "[disabled]";
            var issue = HealthIssue;
            if (!string.IsNullOrEmpty(issue)) return "[error]";
            return "";
        }
    }

    public string? HealthIssue
    {
        get
        {
            if (IsDisabled || string.IsNullOrEmpty(Command)) return null;

            // Check if command executable exists
            if (Path.IsPathRooted(Command))
            {
                return !File.Exists(Command) ? $"Command not found: {Command}" : null;
            }

            // For non-rooted commands like "cmd", "node", "npx", "python" — check PATH
            try
            {
                if (Command.IndexOfAny(['&', '|', '>', '<', ';', '"', '\'', '`', '$', '%', '^']) >= 0)
                {
                    return $"Suspicious command characters: {Command}";
                }
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = $"/c where \"{Command}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p != null)
                {
                    if (!p.WaitForExit(2000))
                    {
                        try { p.Kill(); } catch { }
                        return $"Timeout verifying command: {Command}";
                    }
                    if (p.ExitCode != 0) return $"Command not in PATH: {Command}";
                }
            }
            catch
            {
                return $"Cannot verify command: {Command}";
            }

            return null;
        }
    }

    public bool IsUserEditable => ConfigFile.EndsWith(".claude.json", StringComparison.OrdinalIgnoreCase);

    public string EndpointDisplay => !string.IsNullOrEmpty(Url) ? Url :
        !string.IsNullOrEmpty(Command) ? $"{Command} {Args}".Trim() : "";
}
