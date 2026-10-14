using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests;

public class SessionWatcherServiceTests
{
    // --- AssignProjectNames: Cwd-based project label disambiguation (Ewa fix) ---

    private static SessionTokenData Sess(string cwd, string fallback = "FALLBACK")
        => new() { Cwd = cwd, ProjectName = fallback };

    [Fact]
    public void AssignProjectNames_DistinctLeaves_UsesLeafOnly()
    {
        var a = Sess(@"D:\XGGProjectsGit\GitHub");
        var b = Sess(@"D:\XGGProjectsGit\GitHub\claude-sessions-sidekick");
        var c = Sess(@"D:\XGGProjectsGit\GitHub\winget-pkgs-fork");

        SessionWatcherService.AssignProjectNames([a, b, c]);

        Assert.Equal("GitHub", a.ProjectName);
        Assert.Equal("claude-sessions-sidekick", b.ProjectName);
        Assert.Equal("winget-pkgs-fork", c.ProjectName);
    }

    [Fact]
    public void AssignProjectNames_CollidingLeaves_ExtendsWithParent()
    {
        var a = Sess(@"C:\Praca\KlientA\Frontend");
        var b = Sess(@"C:\Praca\KlientB\Frontend");

        SessionWatcherService.AssignProjectNames([a, b]);

        // Leaf "Frontend" collides, so both grow left by one segment — and no more.
        Assert.Equal(@"KlientA\Frontend", a.ProjectName);
        Assert.Equal(@"KlientB\Frontend", b.ProjectName);
    }

    [Fact]
    public void AssignProjectNames_LossyKeyTriple_AllDistinct()
    {
        // The three real folders that the encoded key collapses to one
        // ("...-ExergyERP-Dev"): underscore, dash, and nested. Ewa's collision.
        var underscore = Sess(@"D:\XGGProjectsGit\ExergyERP_Dev");
        var dash = Sess(@"D:\XGGProjectsGit\ExergyERP-Dev");
        var nested = Sess(@"D:\XGGProjectsGit\ExergyERP\Dev");

        SessionWatcherService.AssignProjectNames([underscore, dash, nested]);

        // All three must end up distinct — the whole point of the fix.
        var labels = new[] { underscore.ProjectName, dash.ProjectName, nested.ProjectName };
        Assert.Equal(3, labels.Distinct().Count());
        // Here the leaves are already distinct (ExergyERP_Dev / ExergyERP-Dev / Dev),
        // so each stays leaf-only — no parent needed.
        Assert.Equal("ExergyERP_Dev", underscore.ProjectName);
        Assert.Equal("ExergyERP-Dev", dash.ProjectName);
        Assert.Equal("Dev", nested.ProjectName);
    }

    [Fact]
    public void AssignProjectNames_SameCwd_SharesLabel()
    {
        var a = Sess(@"D:\Work\App");
        var b = Sess(@"D:\Work\App\");   // trailing separator, same folder

        SessionWatcherService.AssignProjectNames([a, b]);

        Assert.Equal("App", a.ProjectName);
        Assert.Equal("App", b.ProjectName);
    }

    [Fact]
    public void AssignProjectNames_NoCwd_KeepsFallback()
    {
        var a = Sess(cwd: null!, fallback: "key-derived-name");
        a.Cwd = null;

        SessionWatcherService.AssignProjectNames([a]);

        Assert.Equal("key-derived-name", a.ProjectName);
    }

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

    // ── system-generated first messages (MonitorMike's naming report) ──

    [Theory]
    [InlineData("<local-command-caveat>Caveat: The messages below were generated...", true)]
    [InlineData("<command-name>/exit</command-name>", true)]
    [InlineData("<command-message>exit</command-message>", true)]
    [InlineData("<local-command-stdout>Bye!</local-command-stdout>", true)]
    [InlineData("<system-reminder>something</system-reminder>", true)]
    [InlineData("<task-notification>done</task-notification>", true)]
    [InlineData("Caveat: The messages below were generated by the user while running local commands.", true)]
    [InlineData("[Request interrupted by user]", true)]
    [InlineData("  <local-command-caveat>leading whitespace still counts", true)]
    [InlineData("please fix the invoice rounding bug", false)]
    [InlineData("Check <system-reminder> handling in our parser", false)]
    public void IsSystemGeneratedMessage_RecognizesHarnessRecords(string text, bool expected)
    {
        // Act
        var result = SessionWatcherService.IsSystemGeneratedMessage(text);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FirstMessage_SkipsLocalCommandRecords_TakesFirstRealPrompt()
    {
        // Arrange — a session that starts with slash-command noise; the name
        // must come from the first REAL prompt, not the caveat.
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\fake\projects\D--src-app\abc.jsonl";
        var caveat =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:00:00Z","message":{"role":"user","content":"<local-command-caveat>Caveat: The messages below were generated by the user while running local commands.</local-command-caveat>"}}""";
        var command =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:00:01Z","message":{"role":"user","content":"<command-name>/model</command-name>"}}""";
        var realPrompt =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:00:05Z","message":{"role":"user","content":"napraw zaokrąglanie na fakturach"}}""";

        // Act
        watcher.ProcessLine(caveat, fakePath);
        watcher.ProcessLine(command, fakePath);
        watcher.ProcessLine(realPrompt, fakePath);

        // Assert
        var session = Assert.Single(watcher.GetRecentSessions(hours: 24 * 365));
        Assert.Equal("napraw zaokrąglanie na fakturach", session.FirstMessage);
    }

    [Fact]
    public void FirstMessage_ArrayContent_SkipsHarnessBlock_TakesRealPromptFromSameRecord()
    {
        // Arrange — one record whose content array starts with an injected
        // harness block followed by the real prompt.
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\fake\projects\D--src-app\abc.jsonl";
        var line =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:00:00Z","message":{"role":"user","content":[{"type":"text","text":"<system-reminder>context stuff</system-reminder>"},{"type":"text","text":"dodaj walidację NIP"}]}}""";

        // Act
        watcher.ProcessLine(line, fakePath);

        // Assert
        var session = Assert.Single(watcher.GetRecentSessions(hours: 24 * 365));
        Assert.Equal("dodaj walidację NIP", session.FirstMessage);
    }

    [Fact]
    public void LastMeaningfulPrompt_TracksNewestRealPrompt_IgnoresShortAcks()
    {
        // Arrange — a session where the user asks, acknowledges briefly, then
        // asks something new. The label should follow the newest REAL prompt.
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\fake\projects\D--src-app\abc.jsonl";
        var first =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:00:00Z","message":{"role":"user","content":"napraw zaokrąglanie na fakturach"}}""";
        var shortAck =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:10:00Z","message":{"role":"user","content":"ok dawaj"}}""";
        var second =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:20:00Z","message":{"role":"user","content":"teraz dodaj eksport do KSeF"}}""";

        // Act
        watcher.ProcessLine(first, fakePath);
        watcher.ProcessLine(shortAck, fakePath);
        watcher.ProcessLine(second, fakePath);

        // Assert — first message stays first, label follows the latest ask.
        var session = Assert.Single(watcher.GetRecentSessions(hours: 24 * 365));
        Assert.Equal("napraw zaokrąglanie na fakturach", session.FirstMessage);
        Assert.Equal("teraz dodaj eksport do KSeF", session.LastMeaningfulPrompt);
        Assert.Equal("teraz dodaj eksport do KSeF", session.Topic);
    }

    [Fact]
    public void LastMeaningfulPrompt_ShortAckOnly_KeepsPreviousPrompt()
    {
        // Arrange
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\fake\projects\D--src-app\abc.jsonl";
        var first =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:00:00Z","message":{"role":"user","content":"napraw zaokrąglanie na fakturach"}}""";
        var shortAck =
            """{"type":"user","sessionId":"sess-1","timestamp":"2026-10-14T08:10:00Z","message":{"role":"user","content":"tak"}}""";

        // Act
        watcher.ProcessLine(first, fakePath);
        watcher.ProcessLine(shortAck, fakePath);

        // Assert
        var session = Assert.Single(watcher.GetRecentSessions(hours: 24 * 365));
        Assert.Equal("napraw zaokrąglanie na fakturach", session.LastMeaningfulPrompt);
    }
}
