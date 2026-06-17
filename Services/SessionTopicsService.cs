using System.IO;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// User-set topic override for a session. Mirrors SessionNotesService's
/// storage pattern but is intentionally separate from session-names.json:
/// that file gets overwritten whenever claude code rewrites the live
/// PID file under ~/.claude/sessions/, which would clobber a manual
/// rename. Topics here only ever change when the user edits them.
///
/// The override takes precedence over CustomName / FirstMessage / Slug
/// in the Topic display chain. Clearing the override (empty string)
/// removes the entry so the original chain takes back over.
/// </summary>
public static class SessionTopicsService
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "session-topics.json");

    private static string _path = DefaultPath;

    private static readonly object _lock = new();
    private static Dictionary<string, string>? _cache;

    private static Dictionary<string, string> LoadUnsafe()
    {
        if (_cache != null)
        {
            return _cache;
        }

        if (!File.Exists(_path))
        {
            _cache = [];
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(_path);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch
        {
            _cache = [];
        }

        return _cache;
    }

    public static string? GetTopic(string sessionId)
    {
        lock (_lock)
        {
            return LoadUnsafe().GetValueOrDefault(sessionId);
        }
    }

    public static void SetTopic(string sessionId, string? topic)
    {
        lock (_lock)
        {
            var topics = LoadUnsafe();
            if (string.IsNullOrWhiteSpace(topic))
            {
                topics.Remove(sessionId);
            }
            else
            {
                topics[sessionId] = topic.Trim();
            }
            SaveUnsafe();
        }
    }

    /// <summary>Test helper: redirect storage to a temp path and clear cache.
    /// Pass null to reset to the production AppData path.</summary>
    internal static void UseStoragePathForTesting(string? path)
    {
        lock (_lock)
        {
            _path = path ?? DefaultPath;
            _cache = null;
        }
    }

    private static void SaveUnsafe()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(_cache ?? []);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_path))
            {
                File.Replace(tempPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        catch
        {
            // Non-critical
        }
    }
}
