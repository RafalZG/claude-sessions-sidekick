using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Shared helpers for editing Claude Code configuration files: CLAUDE.md (global +
/// per-project shared/local) and auto-memory entries under ~/.claude/projects/.
/// Pure static functions, no mutation beyond filesystem writes.
/// </summary>
public static class ClaudeConfigService
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string? _claudeHomeDirOverride;

    /// <summary>
    /// Path to the .claude directory. Defaults to %USERPROFILE%\.claude.
    /// Can be overridden in Settings for non-standard installs (e.g. domain-joined machines
    /// where USERPROFILE has a suffix like .MONITOR).
    /// </summary>
    public static string ClaudeHomeDir
    {
        get => _claudeHomeDirOverride ?? Path.Combine(UserProfile, ".claude");
        set => _claudeHomeDirOverride = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string DetectedClaudeHomeDir => Path.Combine(UserProfile, ".claude");

    public static string GlobalClaudeMdPath => Path.Combine(ClaudeHomeDir, "CLAUDE.md");
    public static string GlobalAgentsDir => Path.Combine(ClaudeHomeDir, "agents");

    public static string GetProjectClaudeMdPath(string projectFolder) =>
        Path.Combine(projectFolder, "CLAUDE.md");

    public static string GetProjectLocalClaudeMdPath(string projectFolder) =>
        Path.Combine(projectFolder, "CLAUDE.local.md");

    public static string GetProjectAgentsDir(string projectFolder) =>
        Path.Combine(projectFolder, ".claude", "agents");

    public static string ClaudeProjectsRoot => Path.Combine(ClaudeHomeDir, "projects");

    /// <summary>
    /// Reads the configured model from Claude Code settings, checking all 4 levels
    /// in priority order (highest wins):
    /// 1. Project .claude/settings.local.json
    /// 2. Project .claude/settings.json
    /// 3. User ~/.claude/settings.local.json
    /// 4. User ~/.claude/settings.json
    /// Returns null if no model is configured (uses Claude default = latest).
    /// </summary>
    public static string? GetConfiguredModel(string? projectRoot = null)
    {
        // Check project-level settings first (highest priority)
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var projLocal = ReadModelFromSettings(Path.Combine(projectRoot, ".claude", "settings.local.json"));
            if (projLocal != null)
            {
                return projLocal.Length == 0 ? null : projLocal;
            }

            var projGlobal = ReadModelFromSettings(Path.Combine(projectRoot, ".claude", "settings.json"));
            if (projGlobal != null)
            {
                return projGlobal.Length == 0 ? null : projGlobal;
            }
        }

        // Then user-level settings
        var userLocal = ReadModelFromSettings(Path.Combine(ClaudeHomeDir, "settings.local.json"));
        if (userLocal != null)
        {
            return userLocal.Length == 0 ? null : userLocal;
        }

        var userGlobal = ReadModelFromSettings(Path.Combine(ClaudeHomeDir, "settings.json"));
        if (userGlobal != null)
        {
            return userGlobal.Length == 0 ? null : userGlobal;
        }

        return null;
    }

    /// <summary>
    /// Returns true if the configured model will use a context window smaller
    /// than 1M. The set of aliases that map to a reduced context window
    /// changed when Opus 4.7 / Sonnet 4.6 launched — current aliases resolve
    /// server-side as follows (per Anthropic's model catalog):
    ///   opus    → Opus 4.8       → 1M context
    ///   sonnet  → Sonnet 4.6     → 1M context
    ///   haiku   → Haiku 4.5      → 200k context
    /// Only the haiku family is still 200k; opus / sonnet shorthands now
    /// resolve to 1M variants and reporting 200k for them produces false
    /// "120% — /compact now" warnings in the tray widget on legitimately
    /// large sessions.
    /// </summary>
    public static bool IsReducedContextModel(string? configuredModel)
    {
        if (string.IsNullOrEmpty(configuredModel))
        {
            return false;
        }

        // Only haiku shorthand still resolves to a 200k model.
        if (configuredModel == "haiku")
        {
            return true;
        }

        // Explicit old model IDs with 200k context (pre-4.6 generation).
        if (configuredModel.Contains("4-5") || configuredModel.Contains("3-5") ||
            configuredModel.Contains("3-opus") || configuredModel.Contains("3-sonnet") ||
            configuredModel.Contains("3-haiku"))
        {
            return true;
        }

        return false;
    }

    // Keep old name for backward compat
    public static bool IsModelShorthand(string? configuredModel) => IsReducedContextModel(configuredModel);

    /// <summary>
    /// Diagnostic: returns the four settings file paths the widget consults
    /// for model resolution along with the model value found in each (or
    /// "(missing)" / "(no model key)"). Used by state dumps to make
    /// model/context-window mis-detection trivially diagnosable from a log.
    /// </summary>
    public static IReadOnlyList<(string Path, string Value)> GetAllConfiguredModelSources(string? projectRoot)
    {
        var paths = new List<string>();
        if (!string.IsNullOrEmpty(projectRoot))
        {
            paths.Add(Path.Combine(projectRoot, ".claude", "settings.local.json"));
            paths.Add(Path.Combine(projectRoot, ".claude", "settings.json"));
        }
        paths.Add(Path.Combine(ClaudeHomeDir, "settings.local.json"));
        paths.Add(Path.Combine(ClaudeHomeDir, "settings.json"));

        var result = new List<(string, string)>();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                result.Add((path, "(missing)"));
                continue;
            }
            var model = ReadModelFromSettings(path);
            result.Add((path, model switch
            {
                null => "(no model key)",
                "" => "(empty string)",
                _ => model
            }));
        }
        return result;
    }

    private static string? ReadModelFromSettings(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("model", out var modelProp))
            {
                return modelProp.GetString() ?? "";
            }
        }
        catch
        {
            // Ignore parse errors
        }
        return null;
    }

    /// <summary>
    /// Reads a text file, returns empty string if it doesn't exist.
    /// </summary>
    public static string LoadText(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : "";
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to read {filePath}: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Writes a text file atomically with a .bak backup of the previous contents.
    /// Creates the parent directory if missing.
    /// Pass <paramref name="skipBackup"/>=true for sensitive files (e.g. credentials)
    /// where a stale .bak with old tokens is a security concern.
    /// </summary>
    public static void SaveText(string filePath, string content, bool skipBackup = false)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!skipBackup && File.Exists(filePath))
        {
            try
            {
                File.Copy(filePath, filePath + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to backup {filePath}: {ex.Message}");
            }
        }

        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, filePath, overwrite: true);

        AppLogger.Info($"Saved {filePath} ({content.Length} chars)");
    }

    // ---- Auto-memory ----

    /// <summary>
    /// Returns a list of Claude Code project directories under ~/.claude/projects/
    /// that have a "memory" subdirectory with at least one .md file.
    /// </summary>
    public static List<MemoryProjectInfo> ListProjectsWithMemory()
    {
        var results = new List<MemoryProjectInfo>();
        if (!Directory.Exists(ClaudeProjectsRoot))
        {
            return results;
        }

        try
        {
            foreach (var projectDir in Directory.GetDirectories(ClaudeProjectsRoot))
            {
                var memoryDir = Path.Combine(projectDir, "memory");
                if (!Directory.Exists(memoryDir))
                {
                    continue;
                }

                // Count .md files (excluding MEMORY.md index)
                var entryCount = Directory.GetFiles(memoryDir, "*.md")
                    .Count(f => !string.Equals(Path.GetFileName(f), "MEMORY.md", StringComparison.OrdinalIgnoreCase));

                if (entryCount == 0)
                {
                    continue;
                }

                results.Add(new MemoryProjectInfo
                {
                    EncodedName = Path.GetFileName(projectDir),
                    MemoryDirectory = memoryDir,
                    EntryCount = entryCount,
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to enumerate Claude projects: {ex.Message}");
        }

        return results.OrderBy(p => p.EncodedName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Loads all memory entries from a memory directory. Skips MEMORY.md (the index).
    /// </summary>
    public static List<MemoryEntry> LoadMemoryEntries(string memoryDir)
    {
        var entries = new List<MemoryEntry>();
        if (!Directory.Exists(memoryDir))
        {
            return entries;
        }

        try
        {
            foreach (var file in Directory.GetFiles(memoryDir, "*.md"))
            {
                if (string.Equals(Path.GetFileName(file), "MEMORY.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var raw = File.ReadAllText(file);
                    var entry = ParseMemoryEntry(raw);
                    entry.FilePath = file;
                    entry.FileName = Path.GetFileName(file);
                    entry.RawContent = raw;
                    entry.LastModified = File.GetLastWriteTime(file);
                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to parse memory entry {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to scan memory dir {memoryDir}: {ex.Message}");
        }

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Parses a memory entry markdown file with YAML-style frontmatter.
    /// Format: --- name: X description: Y type: Z --- body...
    /// </summary>
    public static MemoryEntry ParseMemoryEntry(string raw)
    {
        var entry = new MemoryEntry();

        // Find frontmatter block: starts with --- on its own line, ends with --- on its own line
        var match = Regex.Match(raw, @"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n?(.*)$",
            RegexOptions.Singleline);
        if (!match.Success)
        {
            // No frontmatter - whole thing is body, name/type unknown
            entry.Body = raw.Trim();
            return entry;
        }

        var frontmatter = match.Groups[1].Value;
        entry.Body = match.Groups[2].Value.Trim();

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = trimmed[..colon].Trim().ToLowerInvariant();
            var value = trimmed[(colon + 1)..].Trim();

            switch (key)
            {
                case "name":
                    entry.Name = value;
                    break;
                case "description":
                    entry.Description = value;
                    break;
                case "type":
                    entry.Type = value;
                    break;
                case "tools":
                    entry.Tools = value;
                    break;
            }
        }

        if (string.IsNullOrEmpty(entry.Name))
        {
            entry.Name = "(untitled)";
        }

        return entry;
    }

    // ---- Plugins & Skills ----

    /// <summary>
    /// Lists all plugin sources: marketplace plugins and per-project skills.
    /// </summary>
    public static List<PluginSource> ListPluginSources(List<QuickLaunchEntry> projects)
    {
        var sources = new List<PluginSource>();

        // Marketplace plugins (browsable catalogs)
        // Official marketplace: marketplaces/<name>/plugins/<pluginName>/
        // Team/git marketplaces: marketplaces/<name>/<pluginName>/ (no "plugins" subfolder)
        var mktDir = Path.Combine(ClaudeHomeDir, "plugins", "marketplaces");
        var knownMarketplaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(mktDir))
        {
            foreach (var marketDir in Directory.GetDirectories(mktDir))
            {
                var pluginsSubDir = Path.Combine(marketDir, "plugins");
                var pluginsDir = Directory.Exists(pluginsSubDir) ? pluginsSubDir : marketDir;

                var mktName = Path.GetFileName(marketDir);
                knownMarketplaces.Add(mktName);
                var count = CountPluginDirs(pluginsDir);
                var lastUpdated = GetMarketplaceLastUpdated(mktName);
                var suffix = string.IsNullOrEmpty(lastUpdated) ? "" : $"  - {lastUpdated}";
                sources.Add(new PluginSource
                {
                    DisplayName = $"Marketplace: {mktName}  ({count} plugins){suffix}",
                    SourceType = PluginSourceType.Marketplace,
                    Path = pluginsDir,
                    MarketplaceName = mktName,
                });
            }
        }

        // Cached plugins from team/custom sources not in marketplaces/
        // (installed via Claude Code /plugin but marketplace source only in cache)
        var cacheDir = Path.Combine(ClaudeHomeDir, "plugins", "cache");
        if (Directory.Exists(cacheDir))
        {
            foreach (var sourceDir in Directory.GetDirectories(cacheDir))
            {
                var sourceName = Path.GetFileName(sourceDir);
                if (knownMarketplaces.Contains(sourceName))
                {
                    continue; // Already shown from marketplaces/
                }

                var pluginDirs = Directory.GetDirectories(sourceDir);
                if (pluginDirs.Length == 0)
                {
                    continue;
                }

                sources.Add(new PluginSource
                {
                    DisplayName = $"Installed: {sourceName}  ({pluginDirs.Length} plugins)",
                    SourceType = PluginSourceType.Marketplace,
                    Path = sourceDir,
                    MarketplaceName = sourceName,
                });
            }
        }

        // Per-project skills
        foreach (var p in projects.Where(p => !string.IsNullOrWhiteSpace(p.FolderPath))
                                   .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var skillsDir = Path.Combine(p.FolderPath, ".claude", "skills");
            var count = 0;
            if (Directory.Exists(skillsDir))
            {
                count = Directory.GetDirectories(skillsDir).Length;
            }
            sources.Add(new PluginSource
            {
                DisplayName = $"Project skills: {p.Name}  ({count} skills)",
                SourceType = PluginSourceType.ProjectSkills,
                Path = skillsDir,
            });
        }

        return sources;
    }

    /// <summary>
    /// Lists plugins inside a marketplace plugins directory.
    /// Each subdirectory is one plugin.
    /// </summary>
    public static List<PluginInfo> LoadMarketplacePlugins(string pluginsDir, string marketplaceName = "")
    {
        var result = new List<PluginInfo>();
        if (!Directory.Exists(pluginsDir))
        {
            return result;
        }

        foreach (var rawDir in Directory.GetDirectories(pluginsDir).OrderBy(d => d))
        {
            var rawName = Path.GetFileName(rawDir);
            if (rawName.StartsWith('.'))
            {
                continue;
            }

            // Cache structure: pluginName/version/ — pick the latest version subdir
            // Marketplace structure: pluginName/ (flat, has README.md or .claude-plugin/ directly)
            var dir = rawDir;
            if (!File.Exists(Path.Combine(dir, "README.md")) &&
                !Directory.Exists(Path.Combine(dir, ".claude-plugin")) &&
                !Directory.Exists(Path.Combine(dir, "skills")))
            {
                // Might be cache with version subdirs — pick latest
                var versionDirs = Directory.GetDirectories(dir)
                    .OrderByDescending(d => Directory.GetLastWriteTime(d))
                    .ToArray();
                if (versionDirs.Length > 0)
                {
                    dir = versionDirs[0];
                }
            }

            var name = Path.GetFileName(rawDir);
            var description = "";
            var author = "";

            // Primary source: .claude-plugin/plugin.json manifest
            var manifestPath = Path.Combine(dir, ".claude-plugin", "plugin.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("description", out var descProp))
                    {
                        description = descProp.GetString() ?? "";
                    }
                    if (root.TryGetProperty("author", out var authorProp))
                    {
                        if (authorProp.ValueKind == JsonValueKind.Object &&
                            authorProp.TryGetProperty("name", out var authorName))
                        {
                            author = authorName.GetString() ?? "";
                        }
                        else if (authorProp.ValueKind == JsonValueKind.String)
                        {
                            author = authorProp.GetString() ?? "";
                        }
                    }
                }
                catch
                {
                    // Malformed manifest — fall through to README
                }
            }

            // Fallback: README.md
            if (string.IsNullOrEmpty(description))
            {
                var readme = Path.Combine(dir, "README.md");
                if (File.Exists(readme))
                {
                    var lines = File.ReadAllLines(readme);
                    description = lines.FirstOrDefault(l =>
                        !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#') &&
                        !l.StartsWith("By:", StringComparison.OrdinalIgnoreCase) &&
                        !l.StartsWith("Author:", StringComparison.OrdinalIgnoreCase)) ?? "";

                    if (string.IsNullOrEmpty(author))
                    {
                        var authorLine = lines.FirstOrDefault(l =>
                            l.StartsWith("By:", StringComparison.OrdinalIgnoreCase) ||
                            l.StartsWith("Author:", StringComparison.OrdinalIgnoreCase));
                        if (authorLine != null)
                        {
                            var colonIdx = authorLine.IndexOf(':');
                            if (colonIdx >= 0)
                            {
                                author = authorLine[(colonIdx + 1)..].Trim();
                            }
                        }
                    }
                }
            }

            if (description.Length > 120)
            {
                description = description[..117] + "...";
            }

            // Count sub-components
            var skillsDir = Path.Combine(dir, "skills");
            var cmdsDir = Path.Combine(dir, "commands");
            var agentsDir = Path.Combine(dir, "agents");

            var skillCount = Directory.Exists(skillsDir) ? Directory.GetDirectories(skillsDir).Length : 0;
            var cmdCount = Directory.Exists(cmdsDir) ? Directory.GetFiles(cmdsDir, "*.md").Length : 0;
            var agentCount = Directory.Exists(agentsDir) ? Directory.GetFiles(agentsDir, "*.md").Length : 0;

            var components = new List<string>();
            if (skillCount > 0) components.Add($"{skillCount} skills");
            if (cmdCount > 0) components.Add($"{cmdCount} commands");
            if (agentCount > 0) components.Add($"{agentCount} agents");

            result.Add(new PluginInfo
            {
                Name = name,
                Description = description,
                Author = author,
                Directory = dir,
                Components = string.Join(", ", components),
                HasReadme = File.Exists(Path.Combine(dir, "README.md")) || File.Exists(manifestPath),
                MarketplaceName = marketplaceName,
            });
        }

        return result;
    }

    /// <summary>
    /// Lists skills from a project's .claude/skills/ directory.
    /// Each subdirectory with a SKILL.md is one skill.
    /// </summary>
    public static List<MemoryEntry> LoadProjectSkills(string skillsDir)
    {
        var result = new List<MemoryEntry>();
        if (!Directory.Exists(skillsDir))
        {
            return result;
        }

        foreach (var dir in Directory.GetDirectories(skillsDir).OrderBy(d => d))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            try
            {
                var raw = File.ReadAllText(skillFile);
                var entry = ParseMemoryEntry(raw); // same frontmatter format
                entry.FilePath = skillFile;
                entry.FileName = Path.GetFileName(dir);
                entry.RawContent = raw;
                entry.LastModified = File.GetLastWriteTime(skillFile);
                entry.Type = "skill";

                // List companion files
                var companions = Directory.GetFiles(dir, "*.md")
                    .Where(f => !string.Equals(Path.GetFileName(f), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetFileName(f))
                    .ToList();
                if (companions.Count > 0)
                {
                    entry.Tools = string.Join(", ", companions);
                }

                result.Add(entry);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to parse skill {dir}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Loads a plugin's full content tree: README, skills, commands, agents.
    /// Returns them as a flat list of MemoryEntry for preview.
    /// </summary>
    public static (string readme, List<MemoryEntry> items) LoadPluginDetails(string pluginDir)
    {
        var readme = "";
        var readmePath = Path.Combine(pluginDir, "README.md");
        if (File.Exists(readmePath))
        {
            readme = File.ReadAllText(readmePath);
        }

        var items = new List<MemoryEntry>();

        // Skills
        var skillsDir = Path.Combine(pluginDir, "skills");
        if (Directory.Exists(skillsDir))
        {
            foreach (var dir in Directory.GetDirectories(skillsDir))
            {
                var skillFile = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillFile))
                {
                    continue;
                }

                var raw = File.ReadAllText(skillFile);
                var entry = ParseMemoryEntry(raw);
                entry.FilePath = skillFile;
                entry.FileName = $"skills/{Path.GetFileName(dir)}/SKILL.md";
                entry.RawContent = raw;
                entry.Type = "skill";
                items.Add(entry);
            }
        }

        // Commands (legacy)
        var cmdsDir = Path.Combine(pluginDir, "commands");
        if (Directory.Exists(cmdsDir))
        {
            foreach (var file in Directory.GetFiles(cmdsDir, "*.md"))
            {
                var raw = File.ReadAllText(file);
                var entry = ParseMemoryEntry(raw);
                entry.FilePath = file;
                entry.FileName = $"commands/{Path.GetFileName(file)}";
                entry.RawContent = raw;
                entry.Type = "command";
                items.Add(entry);
            }
        }

        // Agents
        var agentsDir = Path.Combine(pluginDir, "agents");
        if (Directory.Exists(agentsDir))
        {
            foreach (var file in Directory.GetFiles(agentsDir, "*.md"))
            {
                var raw = File.ReadAllText(file);
                var entry = ParseMemoryEntry(raw);
                entry.FilePath = file;
                entry.FileName = $"agents/{Path.GetFileName(file)}";
                entry.RawContent = raw;
                entry.Type = "agent";
                items.Add(entry);
            }
        }

        return (readme, items);
    }

    /// <summary>
    /// Counts plugin directories, excluding dot-directories (.claude, .github, etc.)
    /// </summary>
    private static int CountPluginDirs(string dir)
    {
        return Directory.GetDirectories(dir)
            .Count(d => !Path.GetFileName(d).StartsWith('.'));
    }

    // ---- Plugin state management ----

    private static string PluginsDir => Path.Combine(ClaudeHomeDir, "plugins");
    private static string InstalledPluginsPath => Path.Combine(PluginsDir, "installed_plugins.json");
    private static string BlocklistPath => Path.Combine(PluginsDir, "blocklist.json");
    private static string KnownMarketplacesPath => Path.Combine(PluginsDir, "known_marketplaces.json");

    /// <summary>
    /// Returns installed plugins with their metadata (id → installedVia text).
    /// </summary>
    public static Dictionary<string, string> LoadInstalledPluginsWithMeta()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(InstalledPluginsPath)) return result;
            var json = File.ReadAllText(InstalledPluginsPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Root-level Array format (Claude Desktop): [{"id":"pluginId",...}, ...]
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
                // Object format: {"version":2, "plugins":{"id": ... }}
                // Plugin values can be either:
                //   - Object: {"installedAt":..., "installedVia":...} (widget installs)
                //   - Array:  [{"scope":"project", "installPath":...}] (Claude Desktop installs)
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
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to read installed_plugins.json: {ex.Message}");
        }
        return result;
    }

    /// <summary>Simple set version for quick lookups.</summary>
    public static HashSet<string> LoadInstalledPlugins() =>
        new(LoadInstalledPluginsWithMeta().Keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the list of blocked plugin identifiers.
    /// </summary>
    public static List<BlockedPlugin> LoadBlocklist()
    {
        var result = new List<BlockedPlugin>();
        try
        {
            if (!File.Exists(BlocklistPath)) return result;
            var json = File.ReadAllText(BlocklistPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("plugins", out var plugins) &&
                plugins.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in plugins.EnumerateArray())
                {
                    result.Add(new BlockedPlugin
                    {
                        PluginId = item.GetProperty("plugin").GetString() ?? "",
                        Reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                        Text = item.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "",
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to read blocklist.json: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Adds or removes a plugin from installed_plugins.json.
    /// Uses JsonNode to preserve existing structure.
    /// </summary>
    public static void SetPluginInstalled(string pluginId, bool installed)
    {
        try
        {
            JsonNode? existingNode = null;
            bool isArrayFormat = false;

            if (File.Exists(InstalledPluginsPath))
            {
                var json = File.ReadAllText(InstalledPluginsPath);
                existingNode = JsonNode.Parse(json);
                isArrayFormat = existingNode is JsonArray;
            }

            if (isArrayFormat)
            {
                // Array format: [{"id":"pluginId",...}, ...]
                var arr = existingNode as JsonArray ?? new JsonArray();
                if (installed)
                {
                    bool exists = false;
                    foreach (var item in arr)
                    {
                        if (item is JsonObject obj &&
                            obj.TryGetPropertyValue("id", out var idNode) &&
                            string.Equals(idNode?.GetValue<string>(), pluginId, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        arr.Add(new JsonObject
                        {
                            ["id"] = pluginId,
                            ["installedAt"] = DateTimeOffset.UtcNow.ToString("o"),
                            ["installedVia"] = $"Installed via Claude Sessions Sidekick on {DateTime.Now:yyyy-MM-dd}"
                        });
                    }
                }
                else
                {
                    for (int i = arr.Count - 1; i >= 0; i--)
                    {
                        if (arr[i] is JsonObject obj &&
                            obj.TryGetPropertyValue("id", out var idNode) &&
                            string.Equals(idNode?.GetValue<string>(), pluginId, StringComparison.OrdinalIgnoreCase))
                        {
                            arr.RemoveAt(i);
                        }
                    }
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                var jsonOut = arr.ToJsonString(options);
                var dir = Path.GetDirectoryName(InstalledPluginsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tempPath = InstalledPluginsPath + ".tmp";
                File.WriteAllText(tempPath, jsonOut);
                File.Move(tempPath, InstalledPluginsPath, overwrite: true);
            }
            else
            {
                // Object format: {"version":2, "plugins":{...}}
                var root = existingNode as JsonObject ?? new JsonObject { ["version"] = 2, ["plugins"] = new JsonObject() };
                var plugins = root["plugins"] as JsonObject ?? new JsonObject();
                root["plugins"] = plugins;

                if (installed)
                {
                    if (!plugins.ContainsKey(pluginId))
                    {
                        plugins[pluginId] = new JsonObject
                        {
                            ["installedAt"] = DateTimeOffset.UtcNow.ToString("o"),
                            ["installedVia"] = $"Installed via Claude Sessions Sidekick on {DateTime.Now:yyyy-MM-dd}"
                        };
                    }
                }
                else
                {
                    plugins.Remove(pluginId);
                }

                SaveJsonAtomic(InstalledPluginsPath, root);
            }
            AppLogger.Info($"Plugin {pluginId} {(installed ? "installed" : "uninstalled")}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to update installed_plugins.json", ex);
            throw;
        }
    }

    /// <summary>
    /// Adds or removes a plugin from blocklist.json.
    /// </summary>
    public static void SetPluginBlocked(string pluginId, bool blocked, string reason = "")
    {
        try
        {
            JsonObject root;
            if (File.Exists(BlocklistPath))
            {
                var json = File.ReadAllText(BlocklistPath);
                root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject { ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("o"), ["plugins"] = new JsonArray() };
            }

            var plugins = root["plugins"] as JsonArray ?? new JsonArray();
            root["plugins"] = plugins;

            // Remove existing entry for this plugin
            for (int i = plugins.Count - 1; i >= 0; i--)
            {
                if (plugins[i] is JsonObject obj &&
                    obj.TryGetPropertyValue("plugin", out var p) &&
                    string.Equals(p?.GetValue<string>(), pluginId, StringComparison.OrdinalIgnoreCase))
                {
                    plugins.RemoveAt(i);
                }
            }

            if (blocked)
            {
                plugins.Add(new JsonObject
                {
                    ["plugin"] = pluginId,
                    ["added_at"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["reason"] = string.IsNullOrWhiteSpace(reason) ? "blocked-by-user" : reason,
                    ["text"] = $"Blocked via Claude Sessions Sidekick on {DateTime.Now:yyyy-MM-dd}"
                });
            }

            SaveJsonAtomic(BlocklistPath, root);
            AppLogger.Info($"Plugin {pluginId} {(blocked ? "blocked" : "unblocked")}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to update blocklist.json", ex);
            throw;
        }
    }

    /// <summary>
    /// Returns the last updated timestamp for a marketplace from known_marketplaces.json.
    /// </summary>
    public static string GetMarketplaceLastUpdated(string marketplaceName)
    {
        try
        {
            if (!File.Exists(KnownMarketplacesPath)) return "";
            var json = File.ReadAllText(KnownMarketplacesPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(marketplaceName, out var mkt) &&
                mkt.TryGetProperty("lastUpdated", out var lu))
            {
                if (DateTimeOffset.TryParse(lu.GetString(), out var dt))
                {
                    var ago = DateTimeOffset.UtcNow - dt;
                    if (ago.TotalHours < 1) return $"Updated {(int)ago.TotalMinutes}m ago";
                    if (ago.TotalDays < 1) return $"Updated {(int)ago.TotalHours}h ago";
                    if (ago.TotalDays < 1.5) return "Updated today";
                    return $"Updated {(int)ago.TotalDays}d ago";
                }
            }
        }
        catch { }
        return "";
    }

    private static void SaveJsonAtomic(string filePath, JsonObject root)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = root.ToJsonString(options);
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(filePath)) File.Delete(filePath);
        File.Move(tempPath, filePath);
    }

    // ---- Agents ----

    /// <summary>
    /// Loads all agent definition files (*.md) from a directory.
    /// Same frontmatter format as memory entries (name, description, tools).
    /// </summary>
    public static List<MemoryEntry> LoadAgentEntries(string agentsDir)
    {
        var entries = new List<MemoryEntry>();
        if (!Directory.Exists(agentsDir))
        {
            return entries;
        }

        try
        {
            foreach (var file in Directory.GetFiles(agentsDir, "*.md"))
            {
                try
                {
                    var raw = File.ReadAllText(file);
                    var entry = ParseMemoryEntry(raw);
                    entry.FilePath = file;
                    entry.FileName = Path.GetFileName(file);
                    entry.RawContent = raw;
                    entry.LastModified = File.GetLastWriteTime(file);
                    entry.Type = "agent";
                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Failed to parse agent {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to scan agents dir {agentsDir}: {ex.Message}");
        }

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Deletes a memory entry file and removes its line from the MEMORY.md index.
    /// The index line is matched by the entry's filename appearing inside (parens).
    /// </summary>
    public static void DeleteMemoryEntry(MemoryEntry entry)
    {
        try
        {
            if (File.Exists(entry.FilePath))
            {
                File.Delete(entry.FilePath);
                AppLogger.Info($"Deleted memory entry {entry.FileName}");
            }

            var memoryDir = Path.GetDirectoryName(entry.FilePath);
            if (memoryDir == null)
            {
                return;
            }

            var indexPath = Path.Combine(memoryDir, "MEMORY.md");
            if (!File.Exists(indexPath))
            {
                return;
            }

            var indexContent = File.ReadAllText(indexPath);
            var lines = indexContent.Split('\n');
            var kept = new List<string>();
            bool removed = false;

            foreach (var line in lines)
            {
                // Match "- [Title](filename.md) — ..." format
                if (!removed && line.Contains($"({entry.FileName})", StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    continue;
                }
                kept.Add(line);
            }

            if (removed)
            {
                File.WriteAllText(indexPath, string.Join('\n', kept));
                AppLogger.Info($"Removed {entry.FileName} from MEMORY.md index");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to delete memory entry {entry.FileName}", ex);
            throw;
        }
    }
}

public enum PluginSourceType { Marketplace, ProjectSkills }

public class PluginSource
{
    public string DisplayName { get; set; } = "";
    public PluginSourceType SourceType { get; set; }
    public string Path { get; set; } = "";
    public string MarketplaceName { get; set; } = "";
    public override string ToString() => DisplayName;
}

public class PluginInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Directory { get; set; } = "";
    public string Components { get; set; } = "";
    public bool HasReadme { get; set; }
    public string MarketplaceName { get; set; } = "";

    /// <summary>Plugin identifier: "name@marketplace"</summary>
    public string PluginId => $"{Name}@{MarketplaceName}";
    public bool IsInstalled { get; set; }
    public string InstalledVia { get; set; } = "";
    public bool IsBlocked { get; set; }
    public string BlockReason { get; set; } = "";
    public string StatusDisplay => IsBlocked ? "[blocked]" : IsInstalled ? "[installed]" : "";
}

public class BlockedPlugin
{
    public string PluginId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Text { get; set; } = "";
}

public class MemoryProjectInfo
{
    public string EncodedName { get; set; } = "";
    public string MemoryDirectory { get; set; } = "";
    public int EntryCount { get; set; }

    /// <summary>
    /// Decoded human-readable path from the Claude project directory name.
    /// E.g. "D--Projects-MyApp-Main" → "D:\Projects\MyApp\Main"
    /// Note: underscores were replaced with dashes during encoding so we can't
    /// perfectly recover them, but the result is still much more readable.
    /// </summary>
    public string DecodedPath
    {
        get
        {
            // Encoding: "D--rest-with-dashes" where D is drive letter, -- is ":\",
            // and each - in the rest was originally \ / or _
            if (EncodedName.Length >= 3 && EncodedName[1] == '-' && EncodedName[2] == '-')
            {
                var drive = EncodedName[0];
                var rest = EncodedName[3..].Replace('-', '\\');
                return $"{drive}:\\{rest}";
            }
            return EncodedName;
        }
    }

    public string DisplayName => $"{DecodedPath}  ({EntryCount} entries)";
    public override string ToString() => DisplayName;
}
