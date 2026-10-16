using System.IO;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Reads the per-process session files Claude Code keeps in
/// <c>~/.claude/sessions/&lt;pid&gt;.json</c>. Only files whose pid is a LIVE
/// Claude process are returned — files of dead processes linger on disk and
/// must not be trusted. Older Claude Code versions don't write these files at
/// all, so callers always keep a heuristic fallback.
/// </summary>
public static class ClaudeSessionPidFileService
{
    private const long MaxFileBytes = 64 * 1024;

    private static string SessionsDir => Path.Combine(ClaudeConfigService.ClaudeHomeDir, "sessions");

    public static Dictionary<int, ClaudeSessionPidInfo> ReadForPids(IReadOnlyCollection<int> livePids)
    {
        var map = new Dictionary<int, ClaudeSessionPidInfo>();
        try
        {
            if (!Directory.Exists(SessionsDir))
            {
                return map;
            }

            foreach (var pid in livePids)
            {
                var info = ReadOne(pid);
                if (info != null)
                {
                    map[pid] = info;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionPidFile: read failed: {ex.Message}");
        }

        return map;
    }

    /// <summary>Every parseable session file, dead processes included — used to
    /// harvest user-typed names even after the session closed. Each file still
    /// goes through the full Parse guard (pid in the file must match the file
    /// name, GUID session id, sanitized name, 64KB cap); only the liveness and
    /// staleness checks are skipped, because a name is worth keeping after the
    /// process is gone.</summary>
    public static List<ClaudeSessionPidInfo> ReadAllSessionFiles()
    {
        var infos = new List<ClaudeSessionPidInfo>();
        try
        {
            if (!Directory.Exists(SessionsDir))
            {
                return infos;
            }

            foreach (var path in Directory.GetFiles(SessionsDir, "*.json"))
            {
                try
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var pid))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length > MaxFileBytes)
                    {
                        continue;
                    }

                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var info = Parse(json, pid);
                    if (info != null)
                    {
                        infos.Add(info);
                    }
                }
                catch
                {
                    // Skip unreadable files
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionPidFile: read-all failed: {ex.Message}");
        }

        return infos;
    }

    private static ClaudeSessionPidInfo? ReadOne(int pid)
    {
        try
        {
            var path = Path.Combine(SessionsDir, pid + ".json");
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length > MaxFileBytes)
            {
                return null;
            }

            // Windows reuses process IDs: a file last written BEFORE the
            // current process started is leftovers from a dead claude that
            // happened to share the pid — its session is not this process's.
            if (IsOlderThanProcess(fileInfo.LastWriteTimeUtc, pid))
            {
                return null;
            }

            // Claude Code rewrites the file while running — share the handle.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var info = Parse(json, pid);
            return info;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsOlderThanProcess(DateTime fileLastWriteUtc, int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            // A minute of slack: the file is written moments after launch and
            // the two clocks need not agree to the second.
            return fileLastWriteUtc < process.StartTime.ToUniversalTime().AddMinutes(-1);
        }
        catch
        {
            // Can't read the start time — don't reject on that alone.
            return false;
        }
    }

    /// <summary>The name is display-only (balloons, logs, title matching) —
    /// keep it one line, free of control characters and short, so a
    /// hand-edited file can't flood or forge the log.</summary>
    internal static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        // U+2028/U+2029 break lines too but don't count as control characters.
        var chars = name
            .Select(c => char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c)
            .ToArray();
        var cleaned = new string(chars).Trim();
        if (cleaned.Length > 200)
        {
            cleaned = cleaned[..200];
        }

        return cleaned;
    }

    /// <summary>Recognizes Claude's own throwaway session label: the folder
    /// name turned into a slug plus two hex characters, like "exergyerp-dev-6c"
    /// for a session in ExergyERP_Dev. Older Sidekick versions saved these as
    /// if the user had named the session — this lets us clean that up.</summary>
    internal static bool IsDerivedAutoName(string? name, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cwd))
        {
            return false;
        }

        var folder = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(folder))
        {
            return false;
        }

        var slug = SlugForFolder(folder);
        if (name.Length != slug.Length + 3
            || !name.StartsWith(slug + "-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasHexSuffix = Uri.IsHexDigit(name[^2]) && Uri.IsHexDigit(name[^1]);
        return hasHexSuffix;
    }

    /// <summary>Folder name the way Claude writes it inside a derived label:
    /// lowercase, every character that isn't a letter or digit becomes '-'.
    /// If Claude's real algorithm differs for exotic folder names, the only
    /// consequence is that an old junk label survives — a miss never deletes
    /// a name the user typed.</summary>
    private static string SlugForFolder(string folder)
    {
        var chars = folder.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        return slug;
    }

    /// <summary>Parses one pid file. Returns null unless the content carries a
    /// GUID session ID and names the expected pid — a mismatched pid means the
    /// file is stale leftovers from a reused process ID.</summary>
    internal static ClaudeSessionPidInfo? Parse(string json, int expectedPid)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("sessionId", out var sidProp)
                || sidProp.GetString() is not string sessionId
                || !Guid.TryParse(sessionId, out _))
            {
                return null;
            }

            // Claude Code always writes the pid — a file without it, or with a
            // different one, is not this process's record.
            if (!root.TryGetProperty("pid", out var pidProp)
                || !pidProp.TryGetInt32(out var filePid)
                || filePid != expectedPid)
            {
                return null;
            }

            var cwd = root.TryGetProperty("cwd", out var cwdProp) ? cwdProp.GetString() : null;
            var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            name = SanitizeName(name);
            var nameSource = root.TryGetProperty("nameSource", out var nameSourceProp)
                ? nameSourceProp.GetString()
                : null;
            var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;

            var updatedAt = DateTimeOffset.MinValue;
            if (root.TryGetProperty("updatedAt", out var updatedProp)
                && updatedProp.TryGetInt64(out var updatedMs))
            {
                updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(updatedMs);
            }

            var info = new ClaudeSessionPidInfo(expectedPid, sessionId, cwd, name, nameSource, status, updatedAt);
            return info;
        }
        catch
        {
            return null;
        }
    }
}
