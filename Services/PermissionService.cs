using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// One Claude Code settings.json file with its permission rules parsed out.
/// Holds the raw JsonObject so other settings keys (model, env, hooks, etc.) are
/// preserved on save - we only ever rewrite the "permissions" section.
/// </summary>
public class PermissionFile
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string FilePath { get; }
    public string DisplayName { get; }
    public List<PermissionRule> Rules { get; private set; } = new();
    public List<string> AdditionalDirectories { get; private set; } = new();

    private JsonObject _root;

    private PermissionFile(string filePath, string displayName, JsonObject root)
    {
        FilePath = filePath;
        DisplayName = displayName;
        _root = root;
        ParseRules();
    }

    public static PermissionFile Load(string filePath, string displayName)
    {
        JsonObject root;
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    root = new JsonObject();
                }
                else
                {
                    var node = JsonNode.Parse(json);
                    root = node as JsonObject ?? new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to parse {filePath}: {ex.Message}");
            root = new JsonObject();
        }

        return new PermissionFile(filePath, displayName, root);
    }

    private void ParseRules()
    {
        Rules = new List<PermissionRule>();
        AdditionalDirectories = new List<string>();
        if (_root["permissions"] is not JsonObject perms)
        {
            return;
        }

        ParseSection(perms, "allow", PermissionScope.Allow);
        ParseSection(perms, "deny", PermissionScope.Deny);
        ParseSection(perms, "ask", PermissionScope.Ask);

        if (perms["additionalDirectories"] is JsonArray dirs)
        {
            foreach (var item in dirs)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    AdditionalDirectories.Add(s);
                }
            }
        }
    }

    private void ParseSection(JsonObject perms, string key, PermissionScope scope)
    {
        if (perms[key] is not JsonArray arr)
        {
            return;
        }

        foreach (var item in arr)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s))
            {
                var rule = PermissionRule.Parse(s, scope);
                if (rule != null)
                {
                    Rules.Add(rule);
                }
                else
                {
                    AppLogger.Warn($"Skipping malformed permission rule in {DisplayName}: {s}");
                }
            }
        }
    }

    /// <summary>
    /// Persists current rules to disk. Creates a .bak alongside, writes to a .tmp
    /// file then atomically moves it into place. Preserves any non-permissions keys
    /// in the original file.
    /// </summary>
    public void Save()
    {
        // Ensure permissions object exists
        if (_root["permissions"] is not JsonObject perms)
        {
            perms = new JsonObject();
            _root["permissions"] = perms;
        }

        WriteSection(perms, "allow", PermissionScope.Allow);
        WriteSection(perms, "deny", PermissionScope.Deny);
        WriteSection(perms, "ask", PermissionScope.Ask);

        // additionalDirectories: omit the key entirely when empty so we don't pollute
        // settings.json with `"additionalDirectories": []`
        if (AdditionalDirectories.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var d in AdditionalDirectories)
            {
                arr.Add(JsonValue.Create(d));
            }
            perms["additionalDirectories"] = arr;
        }
        else
        {
            perms.Remove("additionalDirectories");
        }

        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Backup existing file (single .bak slot, overwritten each save)
        if (File.Exists(FilePath))
        {
            try
            {
                File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to backup {FilePath}: {ex.Message}");
            }
        }

        var json = _root.ToJsonString(WriteOptions);
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, FilePath, overwrite: true);

        AppLogger.Info($"Saved {Rules.Count} permission rules to {DisplayName}");
    }

    private void WriteSection(JsonObject perms, string key, PermissionScope scope)
    {
        var arr = new JsonArray();
        foreach (var rule in Rules.Where(r => r.Scope == scope))
        {
            arr.Add(JsonValue.Create(rule.RuleString));
        }
        perms[key] = arr;
    }
}

/// <summary>
/// Helpers for resolving Claude Code settings file paths and offering preset rule
/// bundles. Pure functions / static state - no mutation.
/// </summary>
public static class PermissionService
{
    public static string GlobalSettingsPath => Path.Combine(
        ClaudeConfigService.ClaudeHomeDir, "settings.json");

    public static string GetProjectSettingsPath(string projectFolder) =>
        Path.Combine(projectFolder, ".claude", "settings.json");

    public static string GetProjectLocalSettingsPath(string projectFolder) =>
        Path.Combine(projectFolder, ".claude", "settings.local.json");

    public static List<PermissionPreset> Presets { get; } = new()
    {
        new PermissionPreset
        {
            Name = "Essential Tools (Recommended)",
            Description = "Core Claude Code tools that are safe to always allow — eliminates most permission prompts. Recommended as a starting point for all users.",
            AllowRules = new[]
            {
                "Read",
                "Edit",
                "Write",
                "Glob",
                "Grep",
                "WebFetch",
                "WebSearch",
                "Bash(cd *)",
            }
        },
        new PermissionPreset
        {
            Name = "Read-only Explorer",
            Description = "Safe read-only commands: file inspection, search, git status/log/diff",
            AllowRules = new[]
            {
                "Read",
                "Bash(ls *)",
                "Bash(cat *)",
                "Bash(grep *)",
                "Bash(rg *)",
                "Bash(find *)",
                "Bash(git status *)",
                "Bash(git log *)",
                "Bash(git diff *)",
                "Bash(git show *)",
                "Bash(git branch *)",
            }
        },
        new PermissionPreset
        {
            Name = "Safe Dev (Node + .NET)",
            Description = "Read-only + common Node.js / npm and .NET CLI dev commands",
            AllowRules = new[]
            {
                "Read",
                "Bash(ls *)",
                "Bash(cat *)",
                "Bash(grep *)",
                "Bash(find *)",
                "Bash(git status *)",
                "Bash(git log *)",
                "Bash(git diff *)",
                "Bash(npm run *)",
                "Bash(npm test *)",
                "Bash(npm ci *)",
                "Bash(dotnet build *)",
                "Bash(dotnet test *)",
                "Bash(dotnet run *)",
                "Bash(dotnet restore *)",
            }
        },
        new PermissionPreset
        {
            Name = "Block Destructive Git",
            Description = "Adds deny rules for force-push, hard reset, and branch deletion",
            DenyRules = new[]
            {
                "Bash(git push --force *)",
                "Bash(git push -f *)",
                "Bash(git reset --hard *)",
                "Bash(git branch -D *)",
                "Bash(git clean -fd *)",
            }
        },
        new PermissionPreset
        {
            Name = "Block Secret File Reads",
            Description = "Stops Claude from reading .env, credentials, SSH keys, and other secret files - high-value safety rule",
            DenyRules = new[]
            {
                "Read(./.env)",
                "Read(./.env.*)",
                "Read(./*.pem)",
                "Read(./*.key)",
                "Read(./credentials.json)",
                "Read(./secrets/**)",
                "Read(./.aws/**)",
                "Read(~/.aws/credentials)",
                "Read(~/.aws/config)",
                "Read(~/.ssh/id_*)",
                "Read(~/.ssh/known_hosts)",
                "Read(~/.netrc)",
                "Read(~/.npmrc)",
            }
        },
        new PermissionPreset
        {
            Name = "Code Review Mode",
            Description = "Pure read-only - lets Claude review PRs and branches without ability to modify anything",
            AllowRules = new[]
            {
                "Read",
                "Glob",
                "Grep",
                "Bash(git diff *)",
                "Bash(git log *)",
                "Bash(git show *)",
                "Bash(git blame *)",
                "Bash(git shortlog *)",
                "Bash(git status *)",
                "Bash(git branch *)",
                "Bash(grep *)",
                "Bash(find *)",
                "Bash(ls *)",
                "Bash(cat *)",
                "Bash(head *)",
                "Bash(tail *)",
                "Bash(wc *)",
            }
        },
        new PermissionPreset
        {
            Name = "Allow All Read Operations",
            Description = "Match-all for Read/Glob/Grep plus safe file inspection commands - widest possible 'just let Claude read'",
            AllowRules = new[]
            {
                "Read",
                "Glob",
                "Grep",
                "Bash(ls *)",
                "Bash(cat *)",
                "Bash(find *)",
                "Bash(head *)",
                "Bash(tail *)",
                "Bash(wc *)",
                "Bash(file *)",
                "Bash(stat *)",
            }
        },
        new PermissionPreset
        {
            Name = "Git Power User",
            Description = "Wider git operations beyond inspection: stash, checkout, fetch, pull, branch, tag, remote, bisect (read)",
            AllowRules = new[]
            {
                "Bash(git status *)",
                "Bash(git log *)",
                "Bash(git diff *)",
                "Bash(git show *)",
                "Bash(git blame *)",
                "Bash(git stash *)",
                "Bash(git checkout *)",
                "Bash(git fetch *)",
                "Bash(git pull *)",
                "Bash(git branch *)",
                "Bash(git tag *)",
                "Bash(git remote *)",
                "Bash(git bisect log *)",
                "Bash(git bisect view *)",
                "Bash(git rev-parse *)",
                "Bash(git ls-files *)",
            }
        },
    };
}

public class PermissionPreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] AllowRules { get; set; } = Array.Empty<string>();
    public string[] DenyRules { get; set; } = Array.Empty<string>();
}
