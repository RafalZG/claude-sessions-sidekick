using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Tests.Models;

public class SessionTokenDataTests
{
    // ── ContextWindowSize ──────────────────────────────────────────

    public class ContextWindowSizeTests
    {
        [Theory]
        // Modern Opus / Sonnet 4.x — all 1M
        [InlineData("claude-opus-4-6", 1_000_000)]
        [InlineData("claude-opus-4-7", 1_000_000)]
        [InlineData("claude-opus-5-0", 1_000_000)]
        [InlineData("claude-sonnet-4-6", 1_000_000)]
        [InlineData("claude-sonnet-4-7", 1_000_000)]
        [InlineData("claude-sonnet-5-0", 1_000_000)]
        // Haiku family — historically 200k
        [InlineData("claude-haiku-4-5", 200_000)]
        [InlineData("claude-haiku-4-5-20251001", 200_000)]
        // Unknown / null — assume modern (1M) instead of legacy 200k
        [InlineData(null, 1_000_000)]
        // Truly legacy model IDs without "claude-X-" prefix — conservative 200k
        [InlineData("unknown-model", 200_000)]
        public void ModelContextWindow_ReturnsCorrectSize(string? model, long expectedSize)
        {
            // Arrange
            var session = new SessionTokenData { Model = model };

            // Act & Assert
            Assert.Equal(expectedSize, session.ModelContextWindow);
        }

        [Fact]
        public void ContextWindowSize_FallsBackToModelDefault_WhenNoRuntimeData()
        {
            // Arrange
            var session = new SessionTokenData { Model = "claude-opus-4-6" };

            // Act & Assert — no auto-compact data, no max observed → model default
            Assert.Equal(1_000_000, session.ContextWindowSize);
        }

        [Fact]
        public void ContextWindowSize_UsesAutoCompactThreshold_WhenAvailable()
        {
            // Arrange — auto-compact happened at 170k, suggesting ~200k window
            var session = new SessionTokenData
            {
                Model = "claude-opus-4-6",
                AutoCompactThreshold = 170_000
            };

            // Act — 170k / 0.85 = 200k
            var result = session.ContextWindowSize;

            // Assert
            Assert.Equal(200_000, result);
        }

        [Fact]
        public void ContextWindowSize_FallsBackToModelDefault_WhenNoAutoCompact()
        {
            // Arrange
            var session = new SessionTokenData
            {
                Model = "claude-opus-4-6",
                MaxObservedContext = 500_000
            };

            // Act & Assert — no auto-compact, no shorthand → model default
            Assert.Equal(1_000_000, session.ContextWindowSize);
        }

        [Fact]
        public void ContextWindowSize_ShorthandModel_Forces200k()
        {
            // Arrange — "model": "opus" in settings forces 200k even for Opus 4.6
            var session = new SessionTokenData
            {
                Model = "claude-opus-4-6",
                ConfiguredModelIsShorthand = true
            };

            // Act & Assert
            Assert.Equal(200_000, session.ContextWindowSize);
        }

        [Fact]
        public void ContextWindowSize_ShorthandModel_ContextPercentIsRealistic()
        {
            // Arrange — reported scenario: 149.9k context with shorthand "opus"
            var session = new SessionTokenData
            {
                Model = "claude-opus-4-6",
                ConfiguredModelIsShorthand = true,
                LastTurnContextSize = 149_900
            };

            // Act — should show ~75%, not 14%
            var pct = (double)session.LastTurnContextSize / session.ContextWindowSize;

            // Assert
            Assert.InRange(pct, 0.70, 0.80);
        }

        [Fact]
        public void ContextWindowSize_LegacyModelWithReducedContextFlag_Forces200k()
        {
            // Arrange — when ClaudeConfigService.IsReducedContextModel detects an
            // old version in settings (e.g. user has "claude-3-5-sonnet" or
            // "claude-sonnet-4-5"), it sets ConfiguredModelIsShorthand=true,
            // overriding the prefix-based default.
            var session = new SessionTokenData
            {
                Model = "claude-sonnet-4-5-20250514",
                ConfiguredModelIsShorthand = true
            };

            // Act & Assert — shorthand override wins
            Assert.Equal(200_000, session.ContextWindowSize);
        }

        [Fact]
        public void ContextWindowSize_PreClaude4Model_DefaultsTo200k()
        {
            // Arrange — truly old IDs without "claude-{family}-" prefix
            var session = new SessionTokenData
            {
                Model = "claude-3-opus-20240229",
                ConfiguredModelIsShorthand = false
            };

            // Act & Assert — falls through to legacy 200k branch
            Assert.Equal(200_000, session.ContextWindowSize);
        }

        [Fact]
        public void ContextWindowSize_AutoCompact_TakesPriorityOverShorthand()
        {
            // Arrange — auto-compact observed AND shorthand set → auto-compact wins
            var session = new SessionTokenData
            {
                Model = "claude-opus-4-6",
                ConfiguredModelIsShorthand = true,
                AutoCompactThreshold = 170_000
            };

            // Act — auto-compact threshold / 0.85
            var result = session.ContextWindowSize;

            // Assert
            Assert.Equal(200_000, result);
        }
    }

    // ── Topic ──────────────────────────────────────────────────────

    public class TopicTests
    {
        [Fact]
        public void Topic_PrefersCustomName()
        {
            // Arrange
            var session = new SessionTokenData
            {
                CustomName = "My Debug Session",
                FirstMessage = "Fix the bug in...",
                Slug = "keen-mixing-origami",
                SessionId = "abc12345-6789-0000-0000-000000000000"
            };

            // Act & Assert
            Assert.Equal("My Debug Session", session.Topic);
        }

        [Fact]
        public void Topic_PrefersSlugOverFirstMessage()
        {
            // Arrange — slug wins because that's what claude code itself
            // shows in the CLI tab header on resume; matching it makes
            // session cross-reference between the widget and the running
            // tab trivial.
            var session = new SessionTokenData
            {
                FirstMessage = "Fix the bug in...",
                Slug = "keen-mixing-origami",
                SessionId = "abc12345-6789-0000-0000-000000000000"
            };

            // Act & Assert
            Assert.Equal("keen-mixing-origami", session.Topic);
        }

        [Fact]
        public void Topic_FallsBackToFirstMessage_WhenNoSlug()
        {
            // Arrange
            var session = new SessionTokenData
            {
                FirstMessage = "Fix the bug in...",
                SessionId = "abc12345-6789-0000-0000-000000000000"
            };

            // Act & Assert
            Assert.Equal("Fix the bug in...", session.Topic);
        }

        [Fact]
        public void Topic_FallsBackToSlug_WhenOnlySlugSet()
        {
            // Arrange
            var session = new SessionTokenData
            {
                Slug = "keen-mixing-origami",
                SessionId = "abc12345-6789-0000-0000-000000000000"
            };

            // Act & Assert
            Assert.Equal("keen-mixing-origami", session.Topic);
        }

        [Fact]
        public void Topic_FallsBackToSessionIdPrefix()
        {
            // Arrange
            var session = new SessionTokenData
            {
                SessionId = "abc12345-6789-0000-0000-000000000000"
            };

            // Act & Assert
            Assert.Equal("abc12345", session.Topic);
        }

        [Fact]
        public void Topic_ShortSessionId_ReturnsWhole()
        {
            // Arrange
            var session = new SessionTokenData { SessionId = "abc" };

            // Act & Assert
            Assert.Equal("abc", session.Topic);
        }
    }

    // ── LastTurnCacheHitRatio ───────────────────────────────────────

    public class CacheHitRatioTests
    {
        [Fact]
        public void CacheHitRatio_ZeroContext_Returns1()
        {
            // Arrange
            var session = new SessionTokenData { LastTurnContextSize = 0 };

            // Act & Assert
            Assert.Equal(1.0, session.LastTurnCacheHitRatio);
        }

        [Fact]
        public void CacheHitRatio_AllCached_Returns1()
        {
            // Arrange
            var session = new SessionTokenData
            {
                LastTurnContextSize = 100_000,
                LastTurnCacheRead = 100_000
            };

            // Act & Assert
            Assert.Equal(1.0, session.LastTurnCacheHitRatio);
        }

        [Fact]
        public void CacheHitRatio_NonesCached_Returns0()
        {
            // Arrange
            var session = new SessionTokenData
            {
                LastTurnContextSize = 100_000,
                LastTurnCacheRead = 0
            };

            // Act & Assert
            Assert.Equal(0.0, session.LastTurnCacheHitRatio);
        }

        [Fact]
        public void CacheHitRatio_HalfCached_ReturnsHalf()
        {
            // Arrange
            var session = new SessionTokenData
            {
                LastTurnContextSize = 100_000,
                LastTurnCacheRead = 50_000
            };

            // Act & Assert
            Assert.Equal(0.5, session.LastTurnCacheHitRatio);
        }
    }

    // ── ActiveDuration ─────────────────────────────────────────────

    public class ActiveDurationTests
    {
        [Fact]
        public void ActiveDuration_NoTurns_ReturnsZero()
        {
            // Arrange
            var session = new SessionTokenData();

            // Act & Assert
            Assert.Equal(TimeSpan.Zero, session.ActiveDuration);
        }

        [Fact]
        public void ActiveDuration_OneTurn_ReturnsZero()
        {
            // Arrange
            var session = new SessionTokenData
            {
                TurnTimestamps = [DateTimeOffset.UtcNow]
            };

            // Act & Assert
            Assert.Equal(TimeSpan.Zero, session.ActiveDuration);
        }

        [Fact]
        public void ActiveDuration_SmallGaps_SumsAll()
        {
            // Arrange
            var start = new DateTimeOffset(2026, 4, 11, 10, 0, 0, TimeSpan.Zero);
            var session = new SessionTokenData
            {
                TurnTimestamps =
                [
                    start,
                    start.AddMinutes(3),
                    start.AddMinutes(7),
                    start.AddMinutes(9)
                ]
            };

            // Act - all gaps < 10 min: 3+4+2 = 9 minutes
            var result = session.ActiveDuration;

            // Assert
            Assert.Equal(9, result.TotalMinutes);
        }

        [Fact]
        public void ActiveDuration_LargeGap_IsExcluded()
        {
            // Arrange
            var start = new DateTimeOffset(2026, 4, 11, 10, 0, 0, TimeSpan.Zero);
            var session = new SessionTokenData
            {
                TurnTimestamps =
                [
                    start,
                    start.AddMinutes(5),       // gap 5m → counted
                    start.AddMinutes(30),      // gap 25m → idle, excluded
                    start.AddMinutes(35)       // gap 5m → counted
                ]
            };

            // Act - only 5+5 = 10 counted (25m gap excluded)
            var result = session.ActiveDuration;

            // Assert
            Assert.Equal(10, result.TotalMinutes);
        }
    }

    // ── GetRecommendation ──────────────────────────────────────────

    public class GetRecommendationTests
    {
        [Fact]
        public void GetRecommendation_NoContext_ReturnsNone()
        {
            // Arrange
            var session = new SessionTokenData { LastTurnContextSize = 0 };

            // Act
            var rec = session.GetRecommendation();

            // Assert
            Assert.Equal(CompactLevel.None, rec.Level);
        }

        [Fact]
        public void GetRecommendation_Critical_WhenContextAbove75Percent()
        {
            // Arrange - 80% of 200k = 160k context
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001", // 200k window
                LastTurnContextSize = 160_000
            };

            // Act
            var rec = session.GetRecommendation(CompactAggressiveness.Balanced);

            // Assert
            Assert.Equal(CompactLevel.Critical, rec.Level);
            Assert.NotEmpty(rec.Reasons);
        }

        [Fact]
        public void GetRecommendation_Warning_WhenContextAbove50Percent()
        {
            // Arrange - 60% of 200k = 120k context
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001",
                LastTurnContextSize = 120_000
            };

            // Act
            var rec = session.GetRecommendation(CompactAggressiveness.Balanced);

            // Assert
            Assert.Equal(CompactLevel.Warning, rec.Level);
        }

        [Fact]
        public void GetRecommendation_None_WhenContextLow()
        {
            // Arrange - 10% context, few turns
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001",
                LastTurnContextSize = 20_000,
                TurnCount = 3
            };

            // Act
            var rec = session.GetRecommendation(CompactAggressiveness.Balanced);

            // Assert
            Assert.Equal(CompactLevel.None, rec.Level);
        }

        [Fact]
        public void GetRecommendation_AggressiveMode_LowersThresholds()
        {
            // Arrange - 55% context, would be Warning in balanced, Critical in aggressive
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001",
                LastTurnContextSize = 110_000
            };

            // Act
            var balanced = session.GetRecommendation(CompactAggressiveness.Balanced);
            var aggressive = session.GetRecommendation(CompactAggressiveness.Aggressive);

            // Assert
            Assert.Equal(CompactLevel.Warning, balanced.Level);
            Assert.Equal(CompactLevel.Critical, aggressive.Level);
        }

        [Fact]
        public void GetRecommendation_ConservativeMode_RaisesThresholds()
        {
            // Arrange - 55% context, Warning in balanced, None in conservative
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001",
                LastTurnContextSize = 110_000,
                TurnCount = 3
            };

            // Act
            var balanced = session.GetRecommendation(CompactAggressiveness.Balanced);
            var conservative = session.GetRecommendation(CompactAggressiveness.Conservative);

            // Assert
            Assert.Equal(CompactLevel.Warning, balanced.Level);
            Assert.Equal(CompactLevel.None, conservative.Level);
        }

        [Fact]
        public void GetRecommendation_ManyTurns_TriggersHint()
        {
            // Arrange - low context but 60 turns with enough context %
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001",
                LastTurnContextSize = 60_000, // 30% > minPctForDynamic (25%)
                TurnCount = 60               // > turnThreshold (30)
            };

            // Act
            var rec = session.GetRecommendation(CompactAggressiveness.Balanced);

            // Assert
            Assert.Equal(CompactLevel.Hint, rec.Level);
            Assert.Contains(rec.Reasons, r => r.Contains("turns without compacting"));
        }

        [Fact]
        public void GetRecommendation_FastGrowth_TriggersHint()
        {
            // Arrange
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001",
                LastTurnContextSize = 80_000,  // 40% > minPctForDynamic
                PrevTurnContextSize = 20_000,  // growth = 60k > growthThreshold (50k)
                TurnCount = 5
            };

            // Act
            var rec = session.GetRecommendation(CompactAggressiveness.Balanced);

            // Assert
            Assert.Equal(CompactLevel.Hint, rec.Level);
            Assert.Contains(rec.Reasons, r => r.Contains("grew"));
        }

        [Fact]
        public void GetRecommendation_LowCacheHit_TriggersHint()
        {
            // Arrange
            var session = new SessionTokenData
            {
                Model = "claude-haiku-4-5-20251001",
                LastTurnContextSize = 80_000,
                LastTurnCacheRead = 20_000,   // 25% cache hit < threshold (50%)
                TurnCount = 10                // > 5 turns required
            };

            // Act
            var rec = session.GetRecommendation(CompactAggressiveness.Balanced);

            // Assert
            Assert.Equal(CompactLevel.Hint, rec.Level);
            Assert.Contains(rec.Reasons, r => r.Contains("cached"));
        }

        [Fact]
        public void GetRecommendation_OpusModel_Uses1MWindow()
        {
            // Arrange - 160k is 80% of 200k (Critical) but only 16% of 1M (None)
            var session = new SessionTokenData
            {
                Model = "claude-opus-4-6",
                LastTurnContextSize = 160_000,
                TurnCount = 3
            };

            // Act
            var rec = session.GetRecommendation(CompactAggressiveness.Balanced);

            // Assert
            Assert.Equal(CompactLevel.None, rec.Level);
        }
    }

    // ── TotalTokens ────────────────────────────────────────────────

    [Fact]
    public void TotalTokens_SumsAllCategories()
    {
        // Arrange
        var session = new SessionTokenData
        {
            InputTokens = 1000,
            OutputTokens = 500,
            CacheReadTokens = 200,
            CacheCreationTokens = 100
        };

        // Act & Assert
        Assert.Equal(1800, session.TotalTokens);
    }
}
