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
    /// keep it one line and short so a hand-edited file can't flood the log.</summary>
    private static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var cleaned = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleaned.Length > 200)
        {
            cleaned = cleaned[..200];
        }

        return cleaned;
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
            var status = root.TryGetProperty("status", out var statusProp) ? statusProp.GetString() : null;

            var updatedAt = DateTimeOffset.MinValue;
            if (root.TryGetProperty("updatedAt", out var updatedProp)
                && updatedProp.TryGetInt64(out var updatedMs))
            {
                updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(updatedMs);
            }

            var info = new ClaudeSessionPidInfo(expectedPid, sessionId, cwd, name, status, updatedAt);
            return info;
        }
        catch
        {
            return null;
        }
    }
}
