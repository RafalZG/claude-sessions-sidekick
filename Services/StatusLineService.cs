using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSessionsSidekick.Services;

public enum StatusLineState
{
    /// <summary>No statusLine key in settings.json.</summary>
    NotSet,

    /// <summary>statusLine points at Sidekick's generated script.</summary>
    Ours,

    /// <summary>statusLine exists but was configured outside Sidekick.</summary>
    Foreign,

    /// <summary>settings.json exists but could not be parsed — Apply/Remove will refuse to touch it.</summary>
    Unreadable
}

public sealed record StatusLineStatus(StatusLineState State, string? CurrentCommand);

/// <summary>
/// Owns the two artifacts behind "set a status line from Sidekick": the
/// generated PowerShell script in Sidekick's AppData folder and the
/// <c>statusLine</c> key in user-level <c>~/.claude/settings.json</c>.
/// The settings file is round-tripped as a JsonObject (same pattern as
/// <see cref="PermissionFile"/>) so every other key the user has survives
/// untouched, with a .bak backup and tmp+move write. A settings file that
/// exists but cannot be parsed makes Apply/Remove throw instead of silently
/// starting from scratch — writing would wipe every setting the user has.
/// </summary>
public static class StatusLineService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string DefaultScriptPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "statusline.ps1");

    public static string DefaultSettingsPath => Path.Combine(
        ClaudeConfigService.ClaudeHomeDir, "settings.json");

    /// <summary>
    /// The command Claude Code will run. Forward slashes on purpose: Claude Code
    /// on Windows routes the command through Git Bash when it's installed, and
    /// Git Bash eats unquoted backslashes as escapes (documented trap).
    /// The stamp rides along as a positional argument the script ignores: open
    /// sessions refresh the status line immediately only when the command
    /// STRING changes, and without the stamp a re-Apply (same script path)
    /// would leave the old line on screen until the next natural refresh.
    /// </summary>
    internal static string BuildCommand(string scriptPath, string stamp)
    {
        var forwardSlashes = scriptPath.Replace('\\', '/');
        var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{forwardSlashes}\" {stamp}";
        return command;
    }

    public static StatusLineStatus GetStatus(string? settingsPath = null, string? scriptPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var script = scriptPath ?? DefaultScriptPath;

        JsonObject root;
        try
        {
            root = LoadSettingsOrThrow(path);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"StatusLine: failed to read {path}: {ex.Message}");
            var unreadable = new StatusLineStatus(StatusLineState.Unreadable, null);
            return unreadable;
        }

        if (root["statusLine"] is not JsonObject statusLine)
        {
            var notSet = new StatusLineStatus(StatusLineState.NotSet, null);
            return notSet;
        }

        var command = statusLine["command"]?.GetValue<string>();
        var ourMarker = script.Replace('\\', '/');
        if (command != null && command.Contains(ourMarker, StringComparison.OrdinalIgnoreCase))
        {
            var ours = new StatusLineStatus(StatusLineState.Ours, command);
            return ours;
        }
        var foreign = new StatusLineStatus(StatusLineState.Foreign, command);
        return foreign;
    }

    public static void Apply(string template, string? settingsPath = null, string? scriptPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var script = scriptPath ?? DefaultScriptPath;

        // Read (and validate) settings BEFORE touching anything on disk, so a
        // corrupt settings.json aborts the whole operation with no side effects.
        var root = LoadSettingsOrThrow(path);

        var scriptContent = StatusLineScriptGenerator.Generate(template);
        var scriptDir = Path.GetDirectoryName(script);
        if (!string.IsNullOrEmpty(scriptDir))
        {
            Directory.CreateDirectory(scriptDir);
        }
        // UTF-8 WITH BOM: Windows PowerShell 5.1 decodes a BOM-less script in
        // the machine's ANSI codepage, which garbles non-ASCII template text
        // and can even turn a C1 byte into a string-ending quote.
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        File.WriteAllText(script, scriptContent, utf8WithBom);

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var entry = new JsonObject
        {
            ["type"] = "command",
            ["command"] = BuildCommand(script, stamp)
        };
        root["statusLine"] = entry;
        SaveSettings(path, root);
        AppLogger.Info($"StatusLine: applied template ({template.Length} chars) to {path}");
    }

    /// <summary>
    /// Removes the statusLine key (whoever wrote it — the window confirms
    /// before calling this on a foreign entry) and deletes our script file.
    /// </summary>
    public static void Remove(string? settingsPath = null, string? scriptPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var script = scriptPath ?? DefaultScriptPath;

        var root = LoadSettingsOrThrow(path);
        if (root.Remove("statusLine"))
        {
            SaveSettings(path, root);
        }

        try
        {
            if (File.Exists(script))
            {
                File.Delete(script);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"StatusLine: could not delete script: {ex.Message}");
        }
        AppLogger.Info($"StatusLine: removed from {path}");
    }

    /// <summary>
    /// Missing or empty file = fresh empty object (first write creates it).
    /// Existing but unreadable/corrupt file = exception — the caller must not
    /// save over a file it couldn't round-trip.
    /// </summary>
    private static JsonObject LoadSettingsOrThrow(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }
        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }
        var node = JsonNode.Parse(json);
        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException($"{path} does not contain a JSON object at the top level.");
        }
        return obj;
    }

    private static void SaveSettings(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(path))
        {
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"StatusLine: failed to backup {path}: {ex.Message}");
            }
        }

        var json = root.ToJsonString(WriteOptions);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
