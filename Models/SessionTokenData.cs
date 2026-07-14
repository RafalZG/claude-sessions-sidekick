namespace ClaudeSessionsSidekick.Models;

public class SessionTokenData
{
    public string SessionId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? GitBranch { get; set; }
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public int TurnCount { get; set; }

    /// <summary>Last turn's total context size (input + cache_read + cache_creation).</summary>
    public long LastTurnContextSize { get; set; }

    /// <summary>Previous turn's context size, for growth rate calculation.</summary>
    public long PrevTurnContextSize { get; set; }

    /// <summary>Last turn's cache read tokens (for cache hit ratio).</summary>
    public long LastTurnCacheRead { get; set; }

    /// <summary>Last turn's raw input tokens (not cached).</summary>
    public long LastTurnInput { get; set; }

    /// <summary>Last turn's cache creation tokens.</summary>
    public long LastTurnCacheCreation { get; set; }

    /// <summary>Highest context size observed in this session (grows monotonically until compact).</summary>
    public long MaxObservedContext { get; set; }

    /// <summary>Context size just before the last auto-compact. Used to infer actual context window.</summary>
    public long? AutoCompactThreshold { get; set; }

    /// <summary>
    /// Per-session latch so the high-context anomaly warning is logged at most
    /// once per app run. Not serialised — purely runtime state. The first turn
    /// that trips the threshold captures full diagnostic context; subsequent
    /// turns in the same session stay silent to avoid log spam from very long
    /// anomalous sessions (304 turns × 1 warn each = bad).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DiagnosticHighContextLogged { get; set; }

    /// <summary>Custom name set by user via /rename.</summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// True when <see cref="CustomName"/> came from a Claude Code <c>custom-title</c>
    /// record (the user's explicit <c>/rename</c>). Guards against the
    /// session-names.json cache — populated from the ephemeral <c>~/.claude/sessions/*.json</c>
    /// PID files, which hold Claude's AUTO-generated agent name, not a user rename —
    /// clobbering a real rename with a stale auto name. Runtime-only latch, recomputed
    /// each scan from the JSONL, so it never needs to persist.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CustomNameFromRename { get; set; }

    /// <summary>Auto-generated session name, e.g. "keen-mixing-origami".</summary>
    public string? Slug { get; set; }

    /// <summary>First user message text (truncated), for session identification.</summary>
    public string? FirstMessage { get; set; }

    /// <summary>
    /// Best human-readable identifier for this session: custom /rename name → slug
    /// → first user message → first 8 chars of session id. Same priority as Session
    /// Browser. Slug beats FirstMessage because that's what claude code itself shows
    /// in the CLI tab header on resume — using a different label here makes it harder
    /// to cross-reference a session in our list against the tab it's running in.
    /// </summary>
    public string Topic
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName))
            {
                return CustomName!;
            }
            if (!string.IsNullOrWhiteSpace(Slug))
            {
                return Slug!;
            }
            if (!string.IsNullOrWhiteSpace(FirstMessage))
            {
                return FirstMessage!;
            }
            return SessionId.Length >= 8 ? SessionId[..8] : SessionId;
        }
    }

    /// <summary>Timestamps of all assistant turns, for active duration calculation.</summary>
    public List<DateTimeOffset> TurnTimestamps { get; set; } = [];

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;

    /// <summary>
    /// Active working duration - sum of gaps between turns shorter than 10 minutes.
    /// Gaps longer than that are treated as idle/away time.
    /// </summary>
    public TimeSpan ActiveDuration
    {
        get
        {
            if (TurnTimestamps.Count < 2)
            {
                return TimeSpan.Zero;
            }

            var sorted = TurnTimestamps.OrderBy(t => t).ToList();
            var total = TimeSpan.Zero;
            var idleThreshold = TimeSpan.FromMinutes(10);

            for (int i = 1; i < sorted.Count; i++)
            {
                var gap = sorted[i] - sorted[i - 1];
                if (gap < idleThreshold)
                {
                    total += gap;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// Whether a model shorthand (e.g. "opus") is configured in Claude Code settings,
    /// which may force a smaller context window than the latest model version.
    /// Set by the watcher from ClaudeConfigService.
    /// </summary>
    public bool ConfiguredModelIsShorthand { get; set; }

    /// <summary>
    /// Effective context window size for this session. Priority:
    /// 1. Auto-compact threshold / 0.85 — most accurate, derived from actual behavior
    /// 2. Configured model check — shorthand like "opus" forces 200k
    /// 3. Model ID default — full model IDs have known context windows
    /// </summary>
    public long ContextWindowSize
    {
        get
        {
            // If we observed an auto-compact, infer window from it (most accurate)
            // Claude Code auto-compacts at ~80-90% of the window
            if (AutoCompactThreshold is > 0)
            {
                return (long)(AutoCompactThreshold.Value / 0.85);
            }

            // If user has a shorthand like "opus" in settings, Claude Code may use
            // a model version with smaller context (200k instead of 1M)
            if (ConfiguredModelIsShorthand)
            {
                return 200_000;
            }

            return ModelContextWindow;
        }
    }

    /// <summary>
    /// Human-readable explanation of how ContextWindowSize was determined.
    /// Helps users diagnose why widget shows a particular limit.
    /// </summary>
    public string ContextWindowSource
    {
        get
        {
            if (AutoCompactThreshold is > 0)
            {
                return $"inferred from auto-compact at {AutoCompactThreshold:N0}";
            }
            if (ConfiguredModelIsShorthand)
            {
                return "settings.json has model shorthand (200k)";
            }
            return $"model default for {Model ?? "unknown"}";
        }
    }

    /// <summary>
    /// Maximum context window for the model based on its API ID.
    /// Prefix-based so it future-proofs new minor versions
    /// (claude-opus-4-7, claude-sonnet-5-0, etc.) without code changes.
    /// Only accurate when no model shorthand override is active.
    /// </summary>
    public long ModelContextWindow
    {
        get
        {
            if (Model == null)
            {
                // Unknown model — assume modern (1M) instead of the older
                // 200k. New Claude Code installs default to opus/sonnet
                // which both have 1M; over-reporting is preferable to the
                // false-positive /compact suggestions we saw on opus-4-7.
                return 1_000_000;
            }
            // Haiku family historically tops out at 200k.
            if (Model.StartsWith("claude-haiku-", StringComparison.Ordinal))
            {
                return 200_000;
            }
            // Modern Opus / Sonnet 4.x and later, plus Fable 5 / Mythos 5, all
            // support 1M.
            if (Model.StartsWith("claude-opus-", StringComparison.Ordinal) ||
                Model.StartsWith("claude-sonnet-", StringComparison.Ordinal) ||
                Model.StartsWith("claude-fable-", StringComparison.Ordinal) ||
                Model.StartsWith("claude-mythos-", StringComparison.Ordinal))
            {
                return 1_000_000;
            }
            // Genuinely-legacy pre-4 IDs (claude-3-*, claude-2*, claude-instant-*)
            // topped out at 200k.
            if (Model.StartsWith("claude-3", StringComparison.Ordinal) ||
                Model.StartsWith("claude-2", StringComparison.Ordinal) ||
                Model.StartsWith("claude-instant", StringComparison.Ordinal))
            {
                return 200_000;
            }
            // Anything else is almost always a model newer than this build. Assume
            // 1M like the null case above: every modern Claude tier ships at 1M,
            // and over-reporting the window is far less harmful than the
            // false-positive "Consider /compact now" nags a stale 200k default
            // produces. Fable 5 hit exactly this — a 569k session measured
            // against a wrong 200k rendered as "ctx 284%". Haiku, the one modern
            // 200k family, is caught explicitly above.
            return 1_000_000;
        }
    }

    /// <summary>Cache hit ratio for last turn (0.0 - 1.0). Higher = more reuse.</summary>
    public double LastTurnCacheHitRatio
    {
        get
        {
            if (LastTurnContextSize == 0)
            {
                return 1.0;
            }
            return (double)LastTurnCacheRead / LastTurnContextSize;
        }
    }

    public CompactRecommendation GetRecommendation(CompactAggressiveness aggressiveness = CompactAggressiveness.Balanced,
        int customCritical = 75, int customWarning = 50)
    {
        if (LastTurnContextSize == 0)
        {
            return CompactRecommendation.None;
        }

        var pct = (double)LastTurnContextSize / ContextWindowSize;
        var reasons = new List<string>();

        // Thresholds vary by aggressiveness
        var (criticalPct, warningPct, minPctForDynamic, growthThreshold, cacheThreshold, turnThreshold) = aggressiveness switch
        {
            CompactAggressiveness.Conservative => (0.80, 0.65, 0.40, 80_000L, 0.35, 50),
            CompactAggressiveness.Aggressive => (0.50, 0.30, 0.15, 30_000L, 0.60, 15),
            CompactAggressiveness.Custom => (customCritical / 100.0, customWarning / 100.0, customWarning / 200.0,
                                             50_000L, 0.50, 30),
            _ => (0.75, 0.50, 0.25, 50_000L, 0.50, 30) // Balanced
        };

        // Static thresholds
        if (pct >= criticalPct)
        {
            reasons.Add("Running low on context space - responses may degrade or the session could hit its limit soon");
            return new CompactRecommendation(CompactLevel.Critical, reasons);
        }
        if (pct >= warningPct)
        {
            reasons.Add("Over half the context window is used - compacting now frees space and keeps responses sharp");
            return new CompactRecommendation(CompactLevel.Warning, reasons);
        }

        // Dynamic: fast growth per turn
        var growth = LastTurnContextSize - PrevTurnContextSize;
        if (PrevTurnContextSize > 0 && growth > growthThreshold && pct >= minPctForDynamic)
        {
            reasons.Add($"Context grew {growth / 1000}k tokens in one turn - at this rate you'll hit the limit quickly");
        }

        // Dynamic: low cache hit ratio = lots of new/changed context
        if (TurnCount > 5 && LastTurnCacheHitRatio < cacheThreshold && pct >= minPctForDynamic)
        {
            reasons.Add($"Only {LastTurnCacheHitRatio:P0} of context is cached - most tokens are re-sent fresh each turn, burning through your usage faster");
        }

        // Dynamic: many turns without compaction
        if (TurnCount > turnThreshold && pct >= minPctForDynamic)
        {
            reasons.Add($"{TurnCount} turns without compacting - old conversation history is adding up and slowing things down");
        }

        // Session length warning - fires regardless of context %
        var freshSessionThreshold = aggressiveness switch
        {
            CompactAggressiveness.Conservative => 80,
            CompactAggressiveness.Aggressive => 30,
            _ => 50
        };
        if (TurnCount > freshSessionThreshold)
        {
            reasons.Add($"{TurnCount} turns in this session - consider starting a fresh session for better focus and lower token usage");
        }

        // Long duration warning
        var durationHoursThreshold = aggressiveness switch
        {
            CompactAggressiveness.Conservative => 6.0,
            CompactAggressiveness.Aggressive => 2.0,
            _ => 4.0
        };
        if (ActiveDuration.TotalHours >= durationHoursThreshold)
        {
            var hrs = (int)ActiveDuration.TotalHours;
            var mins = ActiveDuration.Minutes;
            reasons.Add($"Session has been active for {hrs}h {mins:D2}m - a fresh session may be more efficient");
        }

        if (reasons.Count > 0)
        {
            return new CompactRecommendation(CompactLevel.Hint, reasons);
        }

        return CompactRecommendation.None;
    }

    public bool IsActive(TimeSpan threshold)
    {
        return DateTimeOffset.UtcNow - LastSeen < threshold;
    }
}

public enum CompactLevel
{
    None,
    Hint,     // light suggestion
    Warning,  // >50% context used
    Critical  // >75% context used
}

public class CompactRecommendation
{
    public static readonly CompactRecommendation None = new(CompactLevel.None, []);

    public CompactLevel Level { get; }
    public List<string> Reasons { get; }

    public CompactRecommendation(CompactLevel level, List<string> reasons)
    {
        Level = level;
        Reasons = reasons;
    }
}

public class AggregatedTokenData
{
    public int ActiveSessionCount { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCacheTokens { get; set; }
    public List<SessionTokenData> Sessions { get; set; } = [];

    public long GrandTotal => TotalInputTokens + TotalOutputTokens + TotalCacheTokens;
}
