using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class BuddyServiceTests
{
    // All tests inject a probe so results don't depend on whether Claude CLI
    // happens to be running on the dev/CI machine.
    private static BuddyService NewSvc(int runningProcessCount = 0) =>
        new(() => runningProcessCount);

    private static UsageData MakeUsage(double weekly = 0, double fiveHour = 0, double? opus = null)
    {
        var data = new UsageData
        {
            FiveHour = new UsageWindow { Utilization = fiveHour },
            SevenDay = new UsageWindow { Utilization = weekly }
        };
        if (opus.HasValue)
        {
            data.SevenDayOpus = new UsageWindow { Utilization = opus.Value };
        }
        return data;
    }

    private static AggregatedTokenData MakeSessions(params SessionTokenData[] sessions)
    {
        return new AggregatedTokenData
        {
            ActiveSessionCount = sessions.Length,
            Sessions = sessions.ToList()
        };
    }

    // ── Happy ──────────────────────────────────────────────────────

    [Fact]
    public void DetermineMood_LowUsageRecentSession_ReturnsHappy()
    {
        // Arrange
        var svc = NewSvc(runningProcessCount: 0);
        var usage = MakeUsage(weekly: 0.15, fiveHour: 0.10);
        var sessions = MakeSessions(new SessionTokenData
        {
            Model = "claude-haiku-4-5",
            LastTurnContextSize = 20_000,
            LastSeen = DateTimeOffset.UtcNow
        });

        // Act
        var mood = svc.DetermineMood(usage, sessions);

        // Assert
        Assert.Equal(BuddyMood.Happy, mood);
    }

    // ── Warning ────────────────────────────────────────────────────

    [Fact]
    public void DetermineMood_WeeklyOver80_ReturnsWarning()
    {
        // Arrange
        var svc = NewSvc();
        var usage = MakeUsage(weekly: 0.85);

        // Act
        var mood = svc.DetermineMood(usage, null);

        // Assert
        Assert.Equal(BuddyMood.Warning, mood);
    }

    [Fact]
    public void DetermineMood_OpusOver80_ReturnsWarning()
    {
        // Arrange
        var svc = NewSvc();
        var usage = MakeUsage(weekly: 0.20, opus: 0.90);

        // Act
        var mood = svc.DetermineMood(usage, null);

        // Assert
        Assert.Equal(BuddyMood.Warning, mood);
    }

    [Fact]
    public void DetermineMood_SessionContextOver50_ReturnsWarning()
    {
        // Arrange - 60% of 200k = 120k context
        var svc = NewSvc();
        var sessions = MakeSessions(new SessionTokenData
        {
            Model = "claude-haiku-4-5",
            LastTurnContextSize = 120_000,
            LastSeen = DateTimeOffset.UtcNow
        });

        // Act
        var mood = svc.DetermineMood(null, sessions);

        // Assert
        Assert.Equal(BuddyMood.Warning, mood);
    }

    // ── Thinking ───────────────────────────────────────────────────

    [Fact]
    public void DetermineMood_ClaudeRunningLowUsage_ReturnsThinking()
    {
        // Arrange
        var svc = NewSvc(runningProcessCount: 1);
        var usage = MakeUsage(weekly: 0.15);

        // Act
        var mood = svc.DetermineMood(usage, null);

        // Assert
        Assert.Equal(BuddyMood.Thinking, mood);
    }

    [Fact]
    public void DetermineMood_ClaudeRunningOldSessions_ThinkingBeatsSleeping()
    {
        // Arrange
        var svc = NewSvc(runningProcessCount: 1);
        var sessions = MakeSessions(new SessionTokenData
        {
            Model = "claude-haiku-4-5",
            LastTurnContextSize = 10_000,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-2)
        });

        // Act
        var mood = svc.DetermineMood(null, sessions);

        // Assert
        Assert.Equal(BuddyMood.Thinking, mood);
    }

    [Fact]
    public void DetermineMood_HighUsageClaudeRunning_WarningBeatsThinking()
    {
        // Arrange - Warning is higher priority than Thinking
        var svc = NewSvc(runningProcessCount: 1);
        var usage = MakeUsage(weekly: 0.85);

        // Act
        var mood = svc.DetermineMood(usage, null);

        // Assert
        Assert.Equal(BuddyMood.Warning, mood);
    }

    // ── Sleeping ───────────────────────────────────────────────────

    [Fact]
    public void DetermineMood_NoSessionsNoClaudeRunning_ReturnsSleeping()
    {
        // Arrange
        var svc = NewSvc(runningProcessCount: 0);

        // Act
        var mood = svc.DetermineMood(null, null);

        // Assert
        Assert.Equal(BuddyMood.Sleeping, mood);
    }

    [Fact]
    public void DetermineMood_OldSessionsNoClaudeRunning_ReturnsSleeping()
    {
        // Arrange
        var svc = NewSvc(runningProcessCount: 0);
        var sessions = MakeSessions(new SessionTokenData
        {
            Model = "claude-haiku-4-5",
            LastTurnContextSize = 10_000,
            LastSeen = DateTimeOffset.UtcNow.AddHours(-2)
        });

        // Act
        var mood = svc.DetermineMood(null, sessions);

        // Assert
        Assert.Equal(BuddyMood.Sleeping, mood);
    }

    // ── Alert ──────────────────────────────────────────────────────

    [Fact]
    public void DetermineMood_PendingAlert_BeatsWarning()
    {
        // Arrange - high usage would normally be Warning, but alert takes priority
        var svc = NewSvc();
        svc.SetPendingAlert(true);
        var usage = MakeUsage(weekly: 0.85);

        // Act
        var mood = svc.DetermineMood(usage, null);

        // Assert
        Assert.Equal(BuddyMood.Alert, mood);
    }

    [Fact]
    public void ClearAlert_RemovesPriority()
    {
        // Arrange
        var svc = NewSvc();
        svc.SetPendingAlert(true);
        Assert.Equal(BuddyMood.Alert, svc.DetermineMood(null, null));

        // Act
        svc.SetPendingAlert(false);
        var mood = svc.DetermineMood(null, null);

        // Assert - no longer Alert
        Assert.NotEqual(BuddyMood.Alert, mood);
    }

    [Fact]
    public void ClearAlert_PreservesLastKnownMood_NotFlashHappy()
    {
        // Arrange - establish Warning from usage, raise alert, then clear alert.
        // Without caching last inputs, SetPendingAlert(false) would reset to Happy.
        var svc = NewSvc(runningProcessCount: 0);
        var fired = new List<BuddyMood>();
        svc.MoodChanged += m => fired.Add(m);

        svc.Reevaluate(MakeUsage(weekly: 0.85), null);  // Warning
        svc.SetPendingAlert(true);                       // Alert
        svc.SetPendingAlert(false);                      // back to Warning, not Happy

        // Assert - the mood sequence should be Warning, Alert, Warning
        Assert.Contains(BuddyMood.Warning, fired);
        Assert.Contains(BuddyMood.Alert, fired);
        Assert.Equal(BuddyMood.Warning, svc.CurrentMood);
    }

    // ── MoodChanged event ──────────────────────────────────────────

    [Fact]
    public void Reevaluate_FiresMoodChangedWhenMoodDiffers()
    {
        // Arrange
        var svc = NewSvc();
        var fired = new List<BuddyMood>();
        svc.MoodChanged += m => fired.Add(m);

        // Act - alert flips to Alert
        svc.SetPendingAlert(true);

        // Assert
        Assert.Single(fired);
        Assert.Equal(BuddyMood.Alert, fired[0]);
    }

    [Fact]
    public void Reevaluate_DoesNotFireWhenMoodUnchanged()
    {
        // Arrange
        var svc = NewSvc();
        svc.SetPendingAlert(true);
        var fired = 0;
        svc.MoodChanged += _ => fired++;

        // Act - re-assert alert, mood is already Alert
        svc.SetPendingAlert(true);

        // Assert
        Assert.Equal(0, fired);
    }

    // ── GetMoodLabel ───────────────────────────────────────────────

    [Theory]
    [InlineData(BuddyMood.Happy, "All good")]
    [InlineData(BuddyMood.Sleeping, "Idle")]
    [InlineData(BuddyMood.Thinking, "Claude working")]
    [InlineData(BuddyMood.Warning, "High usage")]
    [InlineData(BuddyMood.Alert, "Needs attention")]
    public void GetMoodLabel_ReturnsExpectedText(BuddyMood mood, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, BuddyService.GetMoodLabel(mood));
    }
}
