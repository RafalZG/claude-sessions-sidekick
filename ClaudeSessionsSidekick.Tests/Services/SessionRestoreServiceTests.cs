using System;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class SessionRestoreServiceTests
{
    private static readonly DateTimeOffset Boot = new(2026, 7, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 8, 5, 0, TimeSpan.Zero);

    private static OpenSessionSnapshot Snap(DateTimeOffset captured, int n = 2)
    {
        var s = new OpenSessionSnapshot { CapturedUtc = captured };
        for (var i = 0; i < n; i++)
        {
            s.Sessions.Add(new OpenSessionRef { SessionId = $"id{i}", Topic = $"t{i}" });
        }
        return s;
    }

    [Fact]
    public void ShouldOfferRestore_CapturedBeforeBoot_AndRecent_True()
    {
        // Snapshot from 10 min before reboot → those sessions died in the reboot.
        var snap = Snap(Boot.AddMinutes(-10));
        Assert.True(SessionRestoreService.ShouldOfferRestore(snap, Boot, Now));
    }

    [Fact]
    public void ShouldOfferRestore_CapturedAfterBoot_False()
    {
        // Snapshot from the current boot session → user closed sessions on purpose.
        var snap = Snap(Boot.AddMinutes(2));
        Assert.False(SessionRestoreService.ShouldOfferRestore(snap, Boot, Now));
    }

    [Fact]
    public void ShouldOfferRestore_Empty_False()
    {
        var snap = Snap(Boot.AddMinutes(-10), n: 0);
        Assert.False(SessionRestoreService.ShouldOfferRestore(snap, Boot, Now));
    }

    [Fact]
    public void ShouldOfferRestore_Null_False()
    {
        Assert.False(SessionRestoreService.ShouldOfferRestore(null, Boot, Now));
    }

    [Fact]
    public void ShouldOfferRestore_TooOld_False()
    {
        // Captured before boot, but the capture is older than MaxSnapshotAge (7 days).
        var captured = Now.AddDays(-10);
        var snap = Snap(captured);
        Assert.False(SessionRestoreService.ShouldOfferRestore(snap, Boot, Now));
    }

    [Fact]
    public void ShouldOfferRestore_WeekendShutdown_True()
    {
        // PC off Friday evening, back Monday morning — 3 days is within MaxSnapshotAge.
        var captured = Now.AddDays(-3);
        var snap = Snap(captured);
        Assert.True(SessionRestoreService.ShouldOfferRestore(snap, Boot, Now));
    }

    [Fact]
    public void ToRef_CopiesRestoreFields()
    {
        var s = new SessionTokenData
        {
            SessionId = "abc",
            Cwd = @"D:\proj",
            ProjectName = "proj",
            LastSeen = Now,
        };

        var r = SessionRestoreService.ToRef(s);

        Assert.Equal("abc", r.SessionId);
        Assert.Equal(@"D:\proj", r.FolderPath);
        Assert.Equal("proj", r.ProjectName);
        Assert.Equal(Now, r.LastSeenUtc);
    }

    [Fact]
    public void BootTimeUtc_IsInThePast()
    {
        // Uptime is always positive, so boot time must be before now.
        Assert.True(SessionRestoreService.BootTimeUtc() <= DateTimeOffset.UtcNow);
    }

    // ── AppendSnapshot (snapshot journal) ──────────────────────────

    [Fact]
    public void AppendSnapshot_AddsEntry_WhenSetChanged()
    {
        // Arrange
        var history = new List<OpenSessionSnapshot> { Snap(Now.AddHours(-1), n: 2) };
        var snapshot = Snap(Now, n: 3);

        // Act
        var updated = SessionRestoreService.AppendSnapshot(history, snapshot, Now);

        // Assert
        Assert.Equal(2, updated.Count);
        Assert.Same(snapshot, updated[^1]);
    }

    [Fact]
    public void AppendSnapshot_ReturnsSameList_WhenSameSessionSet()
    {
        // Arrange — same session IDs, only the capture time moved on.
        var history = new List<OpenSessionSnapshot> { Snap(Now.AddHours(-1), n: 2) };
        var snapshot = Snap(Now, n: 2);

        // Act
        var updated = SessionRestoreService.AppendSnapshot(history, snapshot, Now);

        // Assert — same instance back = caller knows nothing needs saving.
        Assert.Same(history, updated);
    }

    [Fact]
    public void AppendSnapshot_AddsEntry_WhenSameCountButDifferentIds()
    {
        // Arrange — last entry holds two distinct sessions; the new one repeats
        // a single id twice (same count, different set).
        var last = Snap(Now.AddHours(-1), n: 2);
        var history = new List<OpenSessionSnapshot> { last };
        var snapshot = new OpenSessionSnapshot { CapturedUtc = Now };
        snapshot.Sessions.Add(new OpenSessionRef { SessionId = "id0" });
        snapshot.Sessions.Add(new OpenSessionRef { SessionId = "id0" });

        // Act
        var updated = SessionRestoreService.AppendSnapshot(history, snapshot, Now);

        // Assert
        Assert.Equal(2, updated.Count);
        Assert.Same(snapshot, updated[^1]);
    }

    [Fact]
    public void AppendSnapshot_IgnoresEmptySnapshot()
    {
        // Arrange
        var history = new List<OpenSessionSnapshot> { Snap(Now.AddHours(-1), n: 2) };
        var snapshot = Snap(Now, n: 0);

        // Act
        var updated = SessionRestoreService.AppendSnapshot(history, snapshot, Now);

        // Assert
        Assert.Same(history, updated);
    }

    [Fact]
    public void AppendSnapshot_DropsEntriesPastRetention()
    {
        // Arrange — one entry well past the 30-day window, one fresh.
        var history = new List<OpenSessionSnapshot>
        {
            Snap(Now.AddDays(-40), n: 1),
            Snap(Now.AddDays(-2), n: 2),
        };
        var snapshot = Snap(Now, n: 3);

        // Act
        var updated = SessionRestoreService.AppendSnapshot(history, snapshot, Now);

        // Assert
        Assert.Equal(2, updated.Count);
        Assert.Equal(Now.AddDays(-2), updated[0].CapturedUtc);
        Assert.Same(snapshot, updated[^1]);
    }

    [Fact]
    public void AppendSnapshot_CapsEntryCount()
    {
        // Arrange — a full journal of distinct recent entries.
        var history = new List<OpenSessionSnapshot>();
        for (var i = 0; i < SessionRestoreService.MaxHistoryEntries; i++)
        {
            var entry = new OpenSessionSnapshot { CapturedUtc = Now.AddMinutes(-i - 1) };
            entry.Sessions.Add(new OpenSessionRef { SessionId = $"unique{i}" });
            history.Add(entry);
        }
        var snapshot = Snap(Now, n: 1);

        // Act
        var updated = SessionRestoreService.AppendSnapshot(history, snapshot, Now);

        // Assert — oldest dropped, newest kept.
        Assert.Equal(SessionRestoreService.MaxHistoryEntries, updated.Count);
        Assert.Same(snapshot, updated[^1]);
    }

    // ── EncodeProjectDirName ───────────────────────────────────────

    [Theory]
    [InlineData(@"D:\XGGProjectsGit\ExergyERP_Dev", "D--XGGProjectsGit-ExergyERP-Dev")]
    [InlineData(@"C:\Users\john.doe\my app", "C--Users-john-doe-my-app")]
    [InlineData(@"D:\proj\", "D--proj")]
    [InlineData("D:/proj/sub", "D--proj-sub")]
    public void EncodeProjectDirName_MatchesClaudeFolderNaming(string cwd, string expected)
    {
        // Act
        var encoded = SessionRestoreService.EncodeProjectDirName(cwd);

        // Assert
        Assert.Equal(expected, encoded);
    }
}
