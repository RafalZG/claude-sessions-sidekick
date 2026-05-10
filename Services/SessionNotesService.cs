using System.IO;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Per-session free-form notes. Mirrors SessionColorService's storage pattern
/// (locked dictionary, atomic write). Notes survive Claude Code's /rename
/// because they're keyed by session ID, not by topic text.
/// </summary>
public static class SessionNotesService
{
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "session-notes.json");

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

    public static string? GetNote(string sessionId)
    {
        lock (_lock)
        {
            return LoadUnsafe().GetValueOrDefault(sessionId);
        }
    }

    public static void SetNote(string sessionId, string? note)
    {
        lock (_lock)
        {
            var notes = LoadUnsafe();
            if (string.IsNullOrWhiteSpace(note))
            {
                notes.Remove(sessionId);
            }
            else
            {
                notes[sessionId] = note.Trim();
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
                // Atomic NTFS swap — destination is always a complete file,
                // even on a crash mid-write. File.Delete + File.Move has a
                // window where both files are gone; File.Replace doesn't.
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
