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
        // Captured before boot, but the reboot (and now) is >24h after capture.
        var captured = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero); // 2 days before "now"
        var snap = Snap(captured);
        Assert.False(SessionRestoreService.ShouldOfferRestore(snap, Boot, Now));
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
}
