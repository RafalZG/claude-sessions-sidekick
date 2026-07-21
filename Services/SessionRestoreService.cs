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

    /// <summary>Don't offer to restore sessions older than this (avoids resurrecting
    /// a days-old snapshot if Sidekick happens to start long after the reboot).</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromHours(24);

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
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: save failed: {ex.Message}");
        }
    }

    public static OpenSessionSnapshot? Load()
    {
        try
        {
            if (!File.Exists(SnapshotPath))
            {
                return null;
            }
            return JsonSerializer.Deserialize<OpenSessionSnapshot>(File.ReadAllText(SnapshotPath));
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"SessionRestore: load failed: {ex.Message}");
            return null;
        }
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
}
