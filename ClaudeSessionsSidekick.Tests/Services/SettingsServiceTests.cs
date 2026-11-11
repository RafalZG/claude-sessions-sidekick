using System.IO;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class SettingsServiceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SidekickSettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeBackup(string dir, string stamp)
    {
        var path = Path.Combine(dir, $"{SettingsService.BackupPrefix}{stamp}{SettingsService.BackupSuffix}");
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void TrimBackups_KeepsNewestAndDeletesOlder()
    {
        var dir = NewTempDir();
        try
        {
            MakeBackup(dir, "20261101-100000");
            MakeBackup(dir, "20261102-100000");
            MakeBackup(dir, "20261103-100000");
            MakeBackup(dir, "20261104-100000");
            MakeBackup(dir, "20261105-100000");

            SettingsService.TrimBackups(dir, SettingsService.BackupsToKeep);

            var remaining = Directory.GetFiles(dir, $"{SettingsService.BackupPrefix}*{SettingsService.BackupSuffix}")
                .Select(Path.GetFileName)
                .OrderBy(n => n)
                .ToList();
            var expected = new[]
            {
                $"{SettingsService.BackupPrefix}20261103-100000{SettingsService.BackupSuffix}",
                $"{SettingsService.BackupPrefix}20261104-100000{SettingsService.BackupSuffix}",
                $"{SettingsService.BackupPrefix}20261105-100000{SettingsService.BackupSuffix}",
            };
            Assert.Equal(expected, remaining);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrimBackups_FewerThanKeep_DeletesNothing()
    {
        var dir = NewTempDir();
        try
        {
            MakeBackup(dir, "20261101-100000");
            MakeBackup(dir, "20261102-100000");

            SettingsService.TrimBackups(dir, SettingsService.BackupsToKeep);

            var count = Directory.GetFiles(dir, $"{SettingsService.BackupPrefix}*{SettingsService.BackupSuffix}").Length;
            Assert.Equal(2, count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrimBackups_LeavesCorruptCopiesAlone()
    {
        var dir = NewTempDir();
        try
        {
            MakeBackup(dir, "20261101-100000");
            MakeBackup(dir, "20261102-100000");
            MakeBackup(dir, "20261103-100000");
            MakeBackup(dir, "20261104-100000");
            var corruptPath = Path.Combine(dir, $"settings.corrupt-20261101-090000{SettingsService.BackupSuffix}");
            File.WriteAllText(corruptPath, "broken");

            SettingsService.TrimBackups(dir, SettingsService.BackupsToKeep);

            Assert.True(File.Exists(corruptPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
