using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests;

public class SessionWatcherServiceTests
{
    // --- LastSeen handling for timestamp-less JSONL lines ---

    [Fact]
    public void ProcessLine_DoesNotBumpLastSeen_WhenTimestampMissing()
    {
        // Arrange
        // In rc4, lines without a "timestamp" field fell back to
        // DateTimeOffset.UtcNow when updating LastSeen, which meant
        // any closed session that received a stray timestamp-less line
        // (custom-title, summary, occasional metadata entries) re-appeared
        // as "active" in the widget for the full 10-min ActiveThreshold.
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\Users\test\.claude\projects\C--Code-demo\session.jsonl";

        var oldTs = DateTimeOffset.UtcNow.AddHours(-2);
        var oldTsStr = oldTs.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var lineWithTs =
            $$$"""{"type":"user","sessionId":"sess-stale","timestamp":"{{{oldTsStr}}}","message":{"role":"user","content":[{"type":"text","text":"first"}]}}""";
        var lineWithoutTs =
            """{"type":"custom-title","sessionId":"sess-stale","customTitle":"Renamed"}""";

        // Act
        watcher.ProcessLine(lineWithTs, fakePath);
        watcher.ProcessLine(lineWithoutTs, fakePath);

        // Assert — LastSeen must still reflect the 2h-old real timestamp,
        // not the now-time the timestamp-less line was processed at.
        var sessions = watcher.GetRecentSessions(hours: 24);
        var session = Assert.Single(sessions);
        var driftFromReal = (session.LastSeen - oldTs).Duration();
        Assert.True(driftFromReal < TimeSpan.FromSeconds(1),
            $"LastSeen drifted {driftFromReal} from the real timestamp — the timestamp-less line bumped it forward.");
    }

    [Fact]
    public void ProcessLine_UpdatesLastSeen_WhenTimestampPresent()
    {
        // Arrange — positive control for the fix above: a line WITH a
        // timestamp must still drive LastSeen forward.
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\Users\test\.claude\projects\C--Code-demo\session.jsonl";

        var firstTs = DateTimeOffset.UtcNow.AddMinutes(-30);
        var secondTs = DateTimeOffset.UtcNow.AddMinutes(-5);
        var firstStr = firstTs.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var secondStr = secondTs.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var first =
            $$$"""{"type":"user","sessionId":"sess-live","timestamp":"{{{firstStr}}}","message":{"role":"user","content":[{"type":"text","text":"hi"}]}}""";
        var second =
            $$$"""{"type":"user","sessionId":"sess-live","timestamp":"{{{secondStr}}}","message":{"role":"user","content":[{"type":"text","text":"and again"}]}}""";

        // Act
        watcher.ProcessLine(first, fakePath);
        watcher.ProcessLine(second, fakePath);

        // Assert
        var session = Assert.Single(watcher.GetRecentSessions(hours: 1));
        var drift = (session.LastSeen - secondTs).Duration();
        Assert.True(drift < TimeSpan.FromSeconds(1),
            $"Expected LastSeen to track the newer line; drifted {drift}.");
    }

    [Fact]
    public void ProcessLine_DoesNotAddTurnTimestamp_WhenTimestampMissing()
    {
        // Arrange — a user-typed line without a timestamp still counts
        // as a real turn (it happened), but we don't make up a fake
        // timestamp for it. TurnTimestamps fuels rate computations and
        // would skew if we polluted it with "now" for unknown actual time.
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\Users\test\.claude\projects\C--Code-demo\session.jsonl";

        var realTs = DateTimeOffset.UtcNow.AddMinutes(-10);
        var realStr = realTs.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var withTs =
            $$$"""{"type":"user","sessionId":"sess-turns","timestamp":"{{{realStr}}}","message":{"role":"user","content":[{"type":"text","text":"first"}]}}""";
        var withoutTs =
            """{"type":"user","sessionId":"sess-turns","message":{"role":"user","content":[{"type":"text","text":"second"}]}}""";

        // Act
        watcher.ProcessLine(withTs, fakePath);
        watcher.ProcessLine(withoutTs, fakePath);

        // Assert
        var session = Assert.Single(watcher.GetRecentSessions(hours: 1));
        Assert.Equal(2, session.TurnCount);
        Assert.Single(session.TurnTimestamps);
        var drift = (session.TurnTimestamps[0] - realTs).Duration();
        Assert.True(drift < TimeSpan.FromSeconds(1),
            $"The recorded turn timestamp drifted {drift} from the real one.");
    }

    [Fact]
    public void ProcessLine_SkipsMalformedTimestamp_WithoutLosingTheLine()
    {
        // Arrange — a malformed "timestamp" should not throw nor fall back
        // to "now"; it should just leave LastSeen alone for this line.
        // (Previously the outer try/catch swallowed the Parse exception,
        // skipping the entire line's content including usage tokens.)
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\Users\test\.claude\projects\C--Code-demo\session.jsonl";

        var oldTs = DateTimeOffset.UtcNow.AddHours(-3);
        var oldStr = oldTs.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        var seed =
            $$$"""{"type":"user","sessionId":"sess-mal","timestamp":"{{{oldStr}}}","message":{"role":"user","content":[{"type":"text","text":"seed"}]}}""";
        var malformed =
            """{"type":"assistant","sessionId":"sess-mal","timestamp":"not-a-real-timestamp","message":{"role":"assistant","model":"claude-opus-4-7","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":50,"output_tokens":10,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""";

        // Act
        watcher.ProcessLine(seed, fakePath);
        watcher.ProcessLine(malformed, fakePath);

        // Assert
        var session = Assert.Single(watcher.GetRecentSessions(hours: 24));
        // LastSeen stayed on the seed line's real timestamp
        var drift = (session.LastSeen - oldTs).Duration();
        Assert.True(drift < TimeSpan.FromSeconds(1),
            $"Malformed timestamp bumped LastSeen by {drift}.");
        // But the usage data from the malformed line was still consumed
        Assert.Equal(50, session.InputTokens);
        Assert.Equal(10, session.OutputTokens);
    }
}
