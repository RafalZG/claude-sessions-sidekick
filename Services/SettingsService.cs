using System.IO;
using System.Text.Json;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick");

    public static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    // How many timestamped backups to keep. Enough to recover from a bad write
    // (e.g. an older build that couldn't read a newer settings file and saved
    // blank defaults back) without letting the backups pile up forever.
    internal const int BackupsToKeep = 3;
    internal const string BackupPrefix = "settings.backup-";
    internal const string BackupSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings != null)
            {
                return settings;
            }

            // Non-empty file that deserialized to null (e.g. a literal "null"):
            // same hazard as a parse exception — preserve it before defaults.
            AppLogger.Warn("Settings deserialized to null — backing up before using defaults");
            BackUpUnreadableSettings();
        }
        catch (Exception ex)
        {
            // A settings file that won't parse must never be silently replaced
            // with blank defaults — the next Save would overwrite it and the
            // user's real settings would be gone. Keep a copy first.
            AppLogger.Error("Settings failed to parse — backing up before using defaults", ex);
            BackUpUnreadableSettings();
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        BackUpCurrentSettings();

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = SettingsPath + ".tmp";
        File.WriteAllText(tempPath, json);
        try
        {
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Don't leave a half-written temp lying next to the real file if
            // the swap failed (destination locked/read-only).
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Nothing more to do; the next save overwrites it anyway.
            }
            throw;
        }
    }

    /// <summary>
    /// Copies the settings file as it stands now to a timestamped backup before
    /// it gets overwritten, then trims old backups to the newest few. The whole
    /// thing is best-effort — a backup failure must never stop the actual save.
    /// </summary>
    private static void BackUpCurrentSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(SettingsDir, $"{BackupPrefix}{stamp}{BackupSuffix}");
            File.Copy(SettingsPath, backupPath, overwrite: true);
            TrimBackups(SettingsDir, BackupsToKeep);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Settings backup skipped: {ex.Message}");
        }
    }

    private static void BackUpUnreadableSettings()
    {
        try
        {
            // A separate prefix, outside the rotated-backup glob, so this copy
            // of an unreadable file is never auto-deleted — it's the one the
            // user most needs to recover from.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(SettingsDir, $"settings.corrupt-{stamp}{BackupSuffix}");
            File.Copy(SettingsPath, backupPath, overwrite: true);
            AppLogger.Warn($"Saved a copy of the unreadable settings to {backupPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not back up the unreadable settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> rotated backups in
    /// <paramref name="dir"/> and deletes the rest. Backups are named with a
    /// sortable <c>yyyyMMdd-HHmmss</c> stamp, so ordering by filename is
    /// newest-first. Corrupt-file copies use a different prefix and are left
    /// alone.
    /// </summary>
    internal static void TrimBackups(string dir, int keep)
    {
        var backups = Directory.GetFiles(dir, $"{BackupPrefix}*{BackupSuffix}")
            .OrderByDescending(path => path)
            .Skip(keep)
            .ToList();
        foreach (var old in backups)
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Could not delete old settings backup '{old}': {ex.Message}");
            }
        }
    }
}
