using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Mini Buddy — determines the widget's "mood" based on state from existing services.
/// Priority (highest first): Alert > Warning > Thinking > Sleeping > Happy.
/// </summary>
public enum BuddyMood
{
    Happy,
    Sleeping,
    Thinking,
    Warning,
    Alert
}

/// <summary>
/// Observes state from other services and publishes a single "mood" value.
/// The mood drives the tray icon and tooltip so the user gets at-a-glance
/// awareness without needing to open the widget.
/// </summary>
public class BuddyService
{
    private static readonly TimeSpan SleepingThreshold = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private volatile bool _hasPendingAlert;
    private BuddyMood _currentMood = BuddyMood.Happy;
    private UsageData? _lastUsage;
    private AggregatedTokenData? _lastSessions;

    // Probe for "is Claude CLI running?" — defaults to the real OS check,
    // but tests inject a deterministic stub to avoid flakiness.
    private readonly Func<int> _runningProcessProbe;

    /// <summary>Fired when the mood changes so the UI can refresh the tray icon.</summary>
    public event Action<BuddyMood>? MoodChanged;

    public BuddyMood CurrentMood => _currentMood;

    public BuddyService() : this(ClaudeProcessService.GetRunningClaudeCodeCount)
    {
    }

    internal BuddyService(Func<int> runningProcessProbe)
    {
        _runningProcessProbe = runningProcessProbe;
    }

    /// <summary>
    /// Set when a permission/compact suggestion toast is shown but not yet dismissed.
    /// Clears when the user clicks or the notification times out.
    /// </summary>
    public void SetPendingAlert(bool hasAlert)
    {
        _hasPendingAlert = hasAlert;
        // Reuse the last known usage/sessions so clearing an alert doesn't flash Happy
        Reevaluate(_lastUsage, _lastSessions);
    }

    /// <summary>
    /// Recalculates the mood based on current usage data and session activity.
    /// Call this from timers and state-change events.
    /// </summary>
    public void Reevaluate(UsageData? usage, AggregatedTokenData? sessions)
    {
        BuddyMood? toRaise = null;
        lock (_gate)
        {
            _lastUsage = usage;
            _lastSessions = sessions;

            var newMood = DetermineMood(usage, sessions);
            if (newMood != _currentMood)
            {
                _currentMood = newMood;
                toRaise = newMood;
            }
        }

        if (toRaise is { } m)
        {
            MoodChanged?.Invoke(m);
        }
    }

    internal BuddyMood DetermineMood(UsageData? usage, AggregatedTokenData? sessions)
    {
        // Priority order (high to low)

        // 1. Alert — pending toast the user should act on
        if (_hasPendingAlert)
        {
            return BuddyMood.Alert;
        }

        // 2. Warning — high context usage in any active session or high weekly usage
        if (IsHighUsage(usage, sessions))
        {
            return BuddyMood.Warning;
        }

        // 3. Thinking — Claude Code process is actively running
        var runningCount = _runningProcessProbe();
        if (runningCount > 0)
        {
            return BuddyMood.Thinking;
        }

        // 4. Sleeping — no Claude activity for a while
        if (IsSleeping(sessions))
        {
            return BuddyMood.Sleeping;
        }

        // 5. Happy — default
        return BuddyMood.Happy;
    }

    private static bool IsHighUsage(UsageData? usage, AggregatedTokenData? sessions)
    {
        // Weekly over 80%
        if (usage?.SevenDay != null && usage.SevenDay.Utilization >= 0.80)
        {
            return true;
        }
        if (usage?.SevenDayOpus != null && usage.SevenDayOpus.Utilization >= 0.80)
        {
            return true;
        }

        // Any active session over 50% context
        if (sessions?.Sessions != null)
        {
            foreach (var s in sessions.Sessions)
            {
                if (s.ContextWindowSize > 0 &&
                    (double)s.LastTurnContextSize / s.ContextWindowSize >= 0.50)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSleeping(AggregatedTokenData? sessions)
    {
        if (sessions?.Sessions == null || sessions.Sessions.Count == 0)
        {
            return true;
        }

        var cutoff = DateTimeOffset.UtcNow - SleepingThreshold;
        foreach (var s in sessions.Sessions)
        {
            if (s.LastSeen > cutoff)
            {
                return false; // Something active
            }
        }
        return true;
    }

    /// <summary>User-facing label for the mood, used in the tray tooltip.</summary>
    public static string GetMoodLabel(BuddyMood mood) => mood switch
    {
        BuddyMood.Alert => "Needs attention",
        BuddyMood.Warning => "High usage",
        BuddyMood.Thinking => "Claude working",
        BuddyMood.Sleeping => "Idle",
        _ => "All good"
    };
}
