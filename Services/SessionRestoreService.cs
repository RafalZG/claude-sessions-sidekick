using System.IO;
using System.Text.Json;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Chrome-style session restore: continuously persists the set of currently-open
/// Claude Code sessions, and after a PC restart offers to reopen the ones that were
/// open when the machine went down.
///
/// The "was it a restart?" signal is the snapshot timestamp vs. system boot time: if
/// the last snapshot was written BEFORE the current boot, those sessions were alive in
/// the previous Windows session and got killed by the reboot — worth offering. If the
/// snapshot is from the current boot, sessions disappearing means the user closed them
/// on purpose, so we stay quiet. This self-clears: once Sidekick writes a fresh
/// post-boot snapshot, the offer condition no longer holds.
/// </summary>
public static class SessionRestoreService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick");

    public static readonly string SnapshotPath = Path.Combine(Dir, "open-sessions.json");

    /// <summary>Journal of past snapshots, so the restore window can also offer
    /// "what was open on Friday morning" — not just the latest state.</summary>
    public static readonly string HistoryPath = Path.Combine(Dir, "open-sessions-history.json");

    /// <summary>Don't offer to restore sessions older than this (avoids resurrecting
    /// a stale snapshot if Sidekick happens to start long after the reboot). A week
    /// covers a PC that stayed off over a weekend or a few days of leave.</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromDays(7);

    /// <summary>How far back the snapshot journal reaches.</summary>
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(30);

    /// <summary>Hard cap on journal entries so the file can't grow without bound.</summary>
    public const int MaxHistoryEntries = 300;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Approximate system boot time = now minus uptime.</summary>
    public static DateTimeOffset BootTimeUtc() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static void Save(IEnumerable<OpenSessionRef> sessions)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var snapshot = new OpenSessionSnapshot
            {
                CapturedUtc = DateTimeOffset.UtcNow,
                Sessions = sessions.ToList(),
            };
            File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(snapshot, JsonOpts));
            UpdateHistory(snapshot);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: save failed: {ex.Message}");
        }
    }

    /// <summary>These files are user-editable JSON — cap what we read so a
    /// corrupted or planted multi-GB file can't be slurped into memory.</summary>
    private const long MaxRestoreFileBytes = 5 * 1024 * 1024;

    public static OpenSessionSnapshot? Load()
    {
        try
        {
            if (!File.Exists(SnapshotPath) || new FileInfo(SnapshotPath).Length > MaxRestoreFileBytes)
            {
                return null;
            }
            var snapshot = JsonSerializer.Deserialize<OpenSessionSnapshot>(File.ReadAllText(SnapshotPath));
            if (snapshot == null)
            {
                return null;
            }

            SanitizeSnapshot(snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops entries a hand-edited (or planted) file could contain:
    /// a null session list, or a session ID that is not a GUID — only GUIDs
    /// may ever reach the relaunch command line.</summary>
    private static void SanitizeSnapshot(OpenSessionSnapshot snapshot)
    {
        snapshot.Sessions ??= [];
        snapshot.Sessions.RemoveAll(s => s == null || !Guid.TryParse(s.SessionId, out _));
    }

    /// <summary>
    /// True when the snapshot represents sessions that were open before this boot and
    /// is recent enough to still be relevant. Pure function of its inputs — no clock/IO.
    /// </summary>
    public static bool ShouldOfferRestore(OpenSessionSnapshot? snapshot, DateTimeOffset bootUtc, DateTimeOffset nowUtc)
    {
        if (snapshot is null || snapshot.Sessions.Count == 0)
        {
            return false;
        }

        // Captured during the current boot session → sessions closing was intentional.
        if (snapshot.CapturedUtc >= bootUtc)
        {
            return false;
        }

        // Too old to be worth resurrecting.
        return nowUtc - snapshot.CapturedUtc <= MaxSnapshotAge;
    }

    /// <summary>Maps a live session to a restorable reference.</summary>
    public static OpenSessionRef ToRef(SessionTokenData s) => new()
    {
        SessionId = s.SessionId,
        FolderPath = s.Cwd,
        ProjectName = s.ProjectName,
        Topic = s.Topic,
        LastSeenUtc = s.LastSeen,
    };

    // ---- snapshot journal (last 30 days of "what was open") ----

    /// <summary>Past snapshots, oldest first. Missing/broken file = empty list.</summary>
    public static List<OpenSessionSnapshot> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath) || new FileInfo(HistoryPath).Length > MaxRestoreFileBytes)
            {
                return [];
            }
            var history = JsonSerializer.Deserialize<List<OpenSessionSnapshot>>(File.ReadAllText(HistoryPath));
            if (history == null)
            {
                return [];
            }

            history.RemoveAll(h => h == null);
            foreach (var snapshot in history)
            {
                SanitizeSnapshot(snapshot);
            }
            history.RemoveAll(h => h.Sessions.Count == 0);
            return history;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: history load failed: {ex.Message}");
            return [];
        }
    }

    private static void UpdateHistory(OpenSessionSnapshot snapshot)
    {
        try
        {
            var history = LoadHistory();
            var updated = AppendSnapshot(history, snapshot, DateTimeOffset.UtcNow);
            if (ReferenceEquals(updated, history))
            {
                return;
            }

            // Write via a temp file so a crash mid-write can't wipe 30 days
            // of journal (a half-written file would deserialize to nothing).
            var tempPath = HistoryPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(updated, JsonOpts));
            File.Move(tempPath, HistoryPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: history save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds a snapshot to the journal — but only when the set of open sessions
    /// actually changed, so a quiet day doesn't produce hundreds of identical
    /// entries. Old entries are dropped past the retention window. Pure function
    /// of its inputs; returns the SAME list instance when nothing changed.
    /// </summary>
    public static List<OpenSessionSnapshot> AppendSnapshot(
        List<OpenSessionSnapshot> history
        , OpenSessionSnapshot snapshot
        , DateTimeOffset nowUtc)
    {
        if (snapshot.Sessions.Count == 0)
        {
            return history;
        }

        var last = history.LastOrDefault();
        if (last != null && SameSessionSet(last, snapshot))
        {
            return history;
        }

        var cutoff = nowUtc - HistoryRetention;
        var updated = history
            .Where(h => h.CapturedUtc >= cutoff)
            .Append(snapshot)
            .ToList();

        if (updated.Count > MaxHistoryEntries)
        {
            updated = updated.Skip(updated.Count - MaxHistoryEntries).ToList();
        }

        return updated;
    }

    private static bool SameSessionSet(OpenSessionSnapshot a, OpenSessionSnapshot b)
    {
        var idsA = a.Sessions
            .Select(s => s.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var idsB = b.Sessions
            .Select(s => s.SessionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return idsA.SetEquals(idsB);
    }

    // ---- fallback lookup on disk ----

    /// <summary>
    /// Claude Code stores each project's sessions in a folder named after the
    /// project path with every character that is not a letter or digit turned
    /// into a dash, e.g. "D:\Projects\My_App" → "D--Projects-My-App".
    /// </summary>
    public static string EncodeProjectDirName(string cwd)
    {
        var trimmed = cwd.Trim().TrimEnd('\\', '/');
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        }

        var encoded = builder.ToString();
        return encoded;
    }

    /// <summary>
    /// Builds a restorable reference for an exact session ID from its file in
    /// the project folder — used when a process's <c>--resume</c> names a
    /// session the watcher doesn't track. No file parsing; the ID and folder
    /// are all a relaunch needs.
    /// </summary>
    public static OpenSessionRef? RefFromSessionId(string cwd, string sessionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cwd) || !Guid.TryParse(sessionId, out _))
            {
                return null;
            }

            var projectDir = Path.Combine(ClaudeConfigService.ClaudeProjectsRoot, EncodeProjectDirName(cwd));
            var filePath = Path.Combine(projectDir, sessionId + ".jsonl");
            var lastWrite = File.Exists(filePath)
                ? new FileInfo(filePath).LastWriteTimeUtc
                : DateTime.UtcNow;

            var reference = MakeDiskRef(cwd, sessionId, lastWrite);
            return reference;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: session-id lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Last resort for a running Claude process whose session the watcher
    /// doesn't know (idle for longer than the watcher looks back): take the
    /// most recently written session file of that project folder that isn't
    /// already claimed by another process.
    /// </summary>
    public static OpenSessionRef? RefFromNewestSessionFile(string cwd, IReadOnlySet<string>? excludeIds = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cwd))
            {
                return null;
            }

            var projectDir = Path.Combine(ClaudeConfigService.ClaudeProjectsRoot, EncodeProjectDirName(cwd));
            if (!Directory.Exists(projectDir))
            {
                return null;
            }

            var newest = Directory.GetFiles(projectDir, "*.jsonl")
                .Select(f => new FileInfo(f))
                .Where(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f.Name), out _))
                .Where(f => excludeIds == null || !excludeIds.Contains(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest == null)
            {
                return null;
            }

            var reference = MakeDiskRef(cwd, Path.GetFileNameWithoutExtension(newest.Name), newest.LastWriteTimeUtc);
            return reference;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: newest-file lookup failed: {ex.Message}");
            return null;
        }
    }

    private static OpenSessionRef MakeDiskRef(string cwd, string sessionId, DateTimeOffset lastSeenUtc)
    {
        var projectName = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        var reference = new OpenSessionRef
        {
            SessionId = sessionId,
            FolderPath = cwd,
            ProjectName = string.IsNullOrEmpty(projectName) ? cwd : projectName,
            Topic = "",
            LastSeenUtc = lastSeenUtc,
        };
        return reference;
    }
}
