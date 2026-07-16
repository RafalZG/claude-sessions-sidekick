using System;
using System.IO;
using System.Linq;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class ScreenshotPasteServiceTests
{
    [Fact]
    public void ResolveSaveDir_NoOverride_UsesAppDataScreenshotsFolder()
    {
        var settings = new AppSettings { ScreenshotSaveDir = null };

        var dir = ScreenshotPasteService.ResolveSaveDir(settings);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeSessionsSidekick",
            "Screenshots");
        Assert.Equal(expected, dir);
    }

    [Fact]
    public void ResolveSaveDir_WithOverride_UsesOverride()
    {
        var custom = Path.Combine(Path.GetTempPath(), "sidekick-shots-custom");
        var settings = new AppSettings { ScreenshotSaveDir = custom };

        Assert.Equal(custom, ScreenshotPasteService.ResolveSaveDir(settings));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void ResolveSaveDir_BlankOverride_FallsBackToDefault(string blank)
    {
        var settings = new AppSettings { ScreenshotSaveDir = blank };

        var dir = ScreenshotPasteService.ResolveSaveDir(settings);

        Assert.EndsWith(Path.Combine("ClaudeSessionsSidekick", "Screenshots"), dir);
    }

    [Fact]
    public void CleanupOldShots_KeepsNewestNAndPrunesTheRest()
    {
        using var temp = new TempDir();
        // 5 shots, oldest → newest by write time.
        var files = CreateShots(temp.Path, 5);
        var settings = new AppSettings { ScreenshotSaveDir = temp.Path, ScreenshotRetentionCount = 2 };

        ScreenshotPasteService.CleanupOldShots(settings);

        var remaining = Directory.GetFiles(temp.Path, "shot_*.png")
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToArray();

        // The two newest (indices 3 and 4) survive.
        Assert.Equal(new[] { Path.GetFileName(files[3]), Path.GetFileName(files[4]) }, remaining);
    }

    [Fact]
    public void CleanupOldShots_RetentionZero_KeepsEverything()
    {
        using var temp = new TempDir();
        CreateShots(temp.Path, 4);
        var settings = new AppSettings { ScreenshotSaveDir = temp.Path, ScreenshotRetentionCount = 0 };

        ScreenshotPasteService.CleanupOldShots(settings);

        Assert.Equal(4, Directory.GetFiles(temp.Path, "shot_*.png").Length);
    }

    [Fact]
    public void CleanupOldShots_FewerThanRetention_KeepsAll()
    {
        using var temp = new TempDir();
        CreateShots(temp.Path, 2);
        var settings = new AppSettings { ScreenshotSaveDir = temp.Path, ScreenshotRetentionCount = 10 };

        ScreenshotPasteService.CleanupOldShots(settings);

        Assert.Equal(2, Directory.GetFiles(temp.Path, "shot_*.png").Length);
    }

    [Fact]
    public void CleanupOldShots_MissingDir_DoesNotThrow()
    {
        var settings = new AppSettings
        {
            ScreenshotSaveDir = Path.Combine(Path.GetTempPath(), "sidekick-shots-does-not-exist-" + Guid.NewGuid()),
            ScreenshotRetentionCount = 3,
        };

        // Should swallow the missing-directory case rather than propagate.
        ScreenshotPasteService.CleanupOldShots(settings);
    }

    // Creates n files named shot_0.png..shot_(n-1).png with strictly increasing
    // LastWriteTimeUtc so ordering is deterministic regardless of filesystem timestamp
    // granularity. Returns the paths in oldest→newest order.
    private static string[] CreateShots(string dir, int n)
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var paths = new string[n];
        for (var i = 0; i < n; i++)
        {
            var p = Path.Combine(dir, $"shot_{i}.png");
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, baseTime.AddMinutes(i));
            paths[i] = p;
        }
        return paths;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sidekick-shots-test-" + Guid.NewGuid());
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
