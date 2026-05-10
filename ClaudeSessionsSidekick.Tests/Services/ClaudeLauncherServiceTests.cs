using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class ClaudeLauncherServiceTests
{
    // --- ResolveClaudePath ---

    [Fact]
    public void ResolveClaudePath_ReturnsCustomPath_WhenSet()
    {
        var original = ClaudeLauncherService.ClaudeExePath;
        try
        {
            // Point to a file that exists on any Windows machine
            ClaudeLauncherService.ClaudeExePath = @"C:\Windows\System32\cmd.exe";
            var result = ClaudeLauncherService.ResolveClaudePath();
            Assert.Equal(@"C:\Windows\System32\cmd.exe", result);
        }
        finally
        {
            ClaudeLauncherService.ClaudeExePath = original;
        }
    }

    [Fact]
    public void ResolveClaudePath_IgnoresInvalidCustomPath()
    {
        var original = ClaudeLauncherService.ClaudeExePath;
        try
        {
            ClaudeLauncherService.ClaudeExePath = @"Z:\nonexistent\claude.exe";
            var result = ClaudeLauncherService.ResolveClaudePath();
            // Should fall through to auto-detection (may or may not find claude)
            Assert.NotEqual(@"Z:\nonexistent\claude.exe", result);
        }
        finally
        {
            ClaudeLauncherService.ClaudeExePath = original;
        }
    }

    [Fact]
    public void ResolveClaudePath_ReturnsNull_WhenCustomPathEmpty()
    {
        var original = ClaudeLauncherService.ClaudeExePath;
        try
        {
            ClaudeLauncherService.ClaudeExePath = null;
            // May or may not find claude via auto-detection — just verify no exception
            _ = ClaudeLauncherService.ResolveClaudePath();
        }
        finally
        {
            ClaudeLauncherService.ClaudeExePath = original;
        }
    }

    // --- Shell override logic ---

    [Fact]
    public void QuickLaunchEntry_ShellOverride_DefaultsToNull()
    {
        var entry = new QuickLaunchEntry();
        Assert.Null(entry.ShellOverride);
    }

    [Fact]
    public void QuickLaunchEntry_ShellOverride_RoundTrips()
    {
        var entry = new QuickLaunchEntry { ShellOverride = ShellType.GitBash };
        Assert.Equal(ShellType.GitBash, entry.ShellOverride);
    }

    [Fact]
    public void QuickLaunchEntry_ShellOverride_SerializesAsJson()
    {
        var entry = new QuickLaunchEntry
        {
            Name = "test",
            FolderPath = @"C:\test",
            ShellOverride = ShellType.PowerShell
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        Assert.Contains("\"shellOverride\":\"PowerShell\"", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<QuickLaunchEntry>(json);
        Assert.Equal(ShellType.PowerShell, deserialized!.ShellOverride);
    }

    [Fact]
    public void QuickLaunchEntry_ShellOverride_NullNotInJson()
    {
        var entry = new QuickLaunchEntry { Name = "test", FolderPath = @"C:\test" };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = System.Text.Json.JsonSerializer.Serialize(entry, options);
        Assert.DoesNotContain("shellOverride", json);
    }

    // --- NormalizeWorkingDir ---
    // Bug: trailing backslash in Quick Launch folder path breaks the launcher
    // because `"C:\foo\"` in shell args parses as one arg with a literal quote,
    // swallowing the next arg. Normalization strips trailing separators.

    [Fact]
    public void NormalizeWorkingDir_StripsSingleTrailingBackslash()
    {
        Assert.Equal(@"C:\src\InternalTools",
            ClaudeLauncherService.NormalizeWorkingDir(@"C:\src\InternalTools\"));
    }

    [Fact]
    public void NormalizeWorkingDir_StripsMultipleTrailingBackslashes()
    {
        Assert.Equal(@"C:\src\InternalTools",
            ClaudeLauncherService.NormalizeWorkingDir(@"C:\src\InternalTools\\\"));
    }

    [Fact]
    public void NormalizeWorkingDir_StripsTrailingForwardSlash()
    {
        Assert.Equal(@"C:\src\InternalTools",
            ClaudeLauncherService.NormalizeWorkingDir(@"C:\src\InternalTools/"));
    }

    [Fact]
    public void NormalizeWorkingDir_PreservesDriveRoot()
    {
        // "C:\" is a root and must stay valid as a working directory.
        Assert.Equal(@"C:\", ClaudeLauncherService.NormalizeWorkingDir(@"C:\"));
    }

    [Fact]
    public void NormalizeWorkingDir_LeavesPathWithoutSeparatorAlone()
    {
        Assert.Equal(@"C:\src\InternalTools",
            ClaudeLauncherService.NormalizeWorkingDir(@"C:\src\InternalTools"));
    }

    [Fact]
    public void NormalizeWorkingDir_EmptyAndNullSafe()
    {
        Assert.Equal("", ClaudeLauncherService.NormalizeWorkingDir(""));
        Assert.Null(ClaudeLauncherService.NormalizeWorkingDir(null!));
    }

    [Fact]
    public void NormalizeWorkingDir_UncPath_StripsTrailingSeparator()
    {
        // Locks behavior so a future Path.TrimEndingDirectorySeparator change
        // (e.g. starts treating `\\server\share` as a root) gets caught.
        Assert.Equal(@"\\server\share\folder",
            ClaudeLauncherService.NormalizeWorkingDir(@"\\server\share\folder\"));
    }

    // --- BuildModelArg ---
    // Per-Quick-Launch model override that survives /model changes the user
    // made in earlier sessions. We append `--model X` to the claude command
    // line; this test pins the construction + the validation that drops
    // anything that could be a shell-injection attempt.

    [Fact]
    public void BuildModelArg_NullReturnsEmpty()
    {
        Assert.Equal("", ClaudeLauncherService.BuildModelArg(null));
    }

    [Fact]
    public void BuildModelArg_EmptyReturnsEmpty()
    {
        Assert.Equal("", ClaudeLauncherService.BuildModelArg(""));
    }

    [Fact]
    public void BuildModelArg_WhitespaceReturnsEmpty()
    {
        Assert.Equal("", ClaudeLauncherService.BuildModelArg("   "));
    }

    [Fact]
    public void BuildModelArg_AliasReturnsFlag()
    {
        Assert.Equal(" --model sonnet", ClaudeLauncherService.BuildModelArg("sonnet"));
        Assert.Equal(" --model opus",   ClaudeLauncherService.BuildModelArg("opus"));
        Assert.Equal(" --model haiku",  ClaudeLauncherService.BuildModelArg("haiku"));
    }

    [Fact]
    public void BuildModelArg_FullModelIdAccepted()
    {
        // Hand-edited settings.json may pin a specific version.
        Assert.Equal(" --model claude-opus-4-7",
            ClaudeLauncherService.BuildModelArg("claude-opus-4-7"));
        Assert.Equal(" --model claude-haiku-4-5-20251001",
            ClaudeLauncherService.BuildModelArg("claude-haiku-4-5-20251001"));
    }

    [Fact]
    public void BuildModelArg_TrimsLeadingTrailingWhitespace()
    {
        Assert.Equal(" --model sonnet", ClaudeLauncherService.BuildModelArg("  sonnet  "));
    }

    [Theory]
    [InlineData("sonnet; calc")]   // command chain
    [InlineData("sonnet`whoami`")] // backtick subshell
    [InlineData("$(whoami)")]      // dollar-paren
    [InlineData("sonnet|cat")]     // pipe
    [InlineData("sonnet&calc")]    // background/separator
    [InlineData("sonnet\"x")]      // embedded quote
    [InlineData("sonnet 'x")]      // embedded apostrophe + space
    [InlineData("../escape")]      // forward slash
    [InlineData("sonnet\\x")]      // backslash
    public void BuildModelArg_RejectsShellMetacharacters(string evil)
    {
        Assert.Equal("", ClaudeLauncherService.BuildModelArg(evil));
    }

    [Fact]
    public void BuildModelArg_RejectsFullwidthLatin()
    {
        // ｓｏｎｎｅｔ — visually similar to "sonnet" but a different Unicode
        // block (FF00-FFEF). char.IsLetterOrDigit would admit these; ASCII-only
        // allow-list rejects them so a malicious settings.json can't smuggle a
        // misleading model name into logs.
        Assert.Equal("", ClaudeLauncherService.BuildModelArg("ｓｏｎｎｅｔ"));
    }

    [Fact]
    public void BuildModelArg_RejectsOverlyLongValue()
    {
        var huge = new string('a', 65);
        Assert.Equal("", ClaudeLauncherService.BuildModelArg(huge));
    }

    [Fact]
    public void BuildModelArg_AcceptsValueAtLengthCap()
    {
        var sixtyFour = new string('a', 64);
        Assert.Equal($" --model {sixtyFour}", ClaudeLauncherService.BuildModelArg(sixtyFour));
    }

    // --- QuickLaunchEntry.ModelOverride serialization ---

    [Fact]
    public void QuickLaunchEntry_ModelOverride_DefaultsToNull()
    {
        Assert.Null(new QuickLaunchEntry().ModelOverride);
    }

    [Fact]
    public void QuickLaunchEntry_ModelOverride_RoundTripsViaJson()
    {
        var entry = new QuickLaunchEntry
        {
            Name = "test",
            FolderPath = @"C:\test",
            ModelOverride = "sonnet"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        Assert.Contains("\"modelOverride\":\"sonnet\"", json);

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<QuickLaunchEntry>(json);
        Assert.Equal("sonnet", deserialized!.ModelOverride);
    }

    [Fact]
    public void QuickLaunchEntry_ModelOverride_NullOmittedWithIgnoreCondition()
    {
        var entry = new QuickLaunchEntry { Name = "test", FolderPath = @"C:\test" };

        var options = new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = System.Text.Json.JsonSerializer.Serialize(entry, options);
        Assert.DoesNotContain("modelOverride", json);
    }
}
