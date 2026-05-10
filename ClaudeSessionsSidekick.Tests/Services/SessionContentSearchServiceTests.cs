using System.IO;
using System.Text;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class SessionContentSearchServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SessionContentSearchServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ContentSearchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteJsonl(string sessionId, params string[] lines)
    {
        var path = Path.Combine(_tempDir, sessionId + ".jsonl");
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // --- SearchAsync ---

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmpty()
    {
        // Arrange
        var path = WriteJsonl("s1", """{"type":"user","message":{"content":"hello"}}""");
        var sessions = new List<(string, string)> { ("s1", path) };

        // Act
        var result = await SessionContentSearchService.SearchAsync(sessions, "", null, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_QueryFoundInSingleFile_ReturnsSessionWithMatch()
    {
        // Arrange
        var path = WriteJsonl("s1",
            """{"type":"user","message":{"content":"discuss ksef integration"}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"sure"}]}}""");
        var sessions = new List<(string, string)> { ("s1", path) };

        // Act
        var result = await SessionContentSearchService.SearchAsync(sessions, "ksef", null, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.True(result.ContainsKey("s1"));
        Assert.Equal(SessionContentMatchSource.User, result["s1"].Source);
        Assert.Contains("ksef", result["s1"].Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchAsync_QueryCaseInsensitive_StillMatches()
    {
        // Arrange
        var path = WriteJsonl("s1", """{"type":"user","message":{"content":"KSeF question"}}""");
        var sessions = new List<(string, string)> { ("s1", path) };

        // Act
        var result = await SessionContentSearchService.SearchAsync(sessions, "ksef", null, CancellationToken.None);

        // Assert
        Assert.True(result.ContainsKey("s1"));
    }

    [Fact]
    public async Task SearchAsync_QueryNotFound_NoMatch()
    {
        // Arrange
        var path = WriteJsonl("s1", """{"type":"user","message":{"content":"unrelated"}}""");
        var sessions = new List<(string, string)> { ("s1", path) };

        // Act
        var result = await SessionContentSearchService.SearchAsync(sessions, "ksef", null, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_MultipleSessions_OnlyMatchingReturned()
    {
        // Arrange
        var matchPath = WriteJsonl("match", """{"type":"user","message":{"content":"about ksef"}}""");
        var skipPath = WriteJsonl("skip", """{"type":"user","message":{"content":"about taxes"}}""");
        var sessions = new List<(string, string)>
        {
            ("match", matchPath),
            ("skip", skipPath)
        };

        // Act
        var result = await SessionContentSearchService.SearchAsync(sessions, "ksef", null, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.True(result.ContainsKey("match"));
        Assert.False(result.ContainsKey("skip"));
    }

    [Fact]
    public async Task SearchAsync_ProgressReportedPerFile()
    {
        // Arrange
        var p1 = WriteJsonl("s1", "{}");
        var p2 = WriteJsonl("s2", "{}");
        var sessions = new List<(string, string)> { ("s1", p1), ("s2", p2) };
        var reports = new List<int>();
        var progress = new Progress<int>(reports.Add);

        // Act
        await SessionContentSearchService.SearchAsync(sessions, "x", progress, CancellationToken.None);
        // Progress reports are dispatched via SynchronizationContext; give them time to drain.
        await Task.Delay(100);

        // Assert
        Assert.Equal(new[] { 1, 2 }, reports);
    }

    [Fact]
    public async Task SearchAsync_MissingFile_SkippedWithoutThrowing()
    {
        // Arrange
        var sessions = new List<(string, string)>
        {
            ("ghost", Path.Combine(_tempDir, "does-not-exist.jsonl"))
        };

        // Act
        var result = await SessionContentSearchService.SearchAsync(sessions, "x", null, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_Cancelled_Throws()
    {
        // Arrange
        var path = WriteJsonl("s1", """{"type":"user","message":{"content":"data"}}""");
        var sessions = new List<(string, string)> { ("s1", path) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act + Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SessionContentSearchService.SearchAsync(sessions, "data", null, cts.Token));
    }

    // --- FindFirstMatchAsync ---

    [Fact]
    public async Task FindFirstMatchAsync_MatchOnLaterLine_FindsIt()
    {
        // Arrange
        var path = WriteJsonl("s1",
            """{"type":"user","message":{"content":"line one"}}""",
            """{"type":"user","message":{"content":"line with KSEF in it"}}""",
            """{"type":"user","message":{"content":"line three"}}""");

        // Act
        var match = await SessionContentSearchService.FindFirstMatchAsync(path, "ksef", CancellationToken.None);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(SessionContentMatchSource.User, match!.Source);
        Assert.Contains("KSEF", match.Excerpt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindFirstMatchAsync_NoMatch_ReturnsNull()
    {
        // Arrange
        var path = WriteJsonl("s1", """{"type":"user","message":{"content":"unrelated"}}""");

        // Act
        var match = await SessionContentSearchService.FindFirstMatchAsync(path, "ksef", CancellationToken.None);

        // Assert
        Assert.Null(match);
    }

    // --- TryExtractMatch: noise filtering ---

    [Fact]
    public void TryExtractMatch_ToolResultWithQuery_ReturnsNull()
    {
        // Arrange — Claude reads MEMORY.md whose content mentions "ksef"
        var line = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"## KSEF Per-User Certificate"}]}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void TryExtractMatch_ToolUseArgsWithQuery_ReturnsNull()
    {
        // Arrange — Bash command argument mentions ksef
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"grep ksef *.cs"}}]}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void TryExtractMatch_ThinkingSignatureWithQuery_ReturnsNull()
    {
        // Arrange — base64 signature happens to contain "kSEF"
        var line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc123kSEFxyz"}]}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void TryExtractMatch_RealUserText_ReturnsUserMatch()
    {
        // Arrange
        var line = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"how do I integrate KSEF?"}]}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.NotNull(match);
        Assert.Equal(SessionContentMatchSource.User, match!.Source);
        Assert.Contains("KSEF", match.Excerpt);
    }

    [Fact]
    public void TryExtractMatch_RealAssistantText_ReturnsAssistantMatch()
    {
        // Arrange
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"To integrate ksef you need..."}]}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.NotNull(match);
        Assert.Equal(SessionContentMatchSource.Assistant, match!.Source);
    }

    [Fact]
    public void TryExtractMatch_StringContent_ReturnsMatch()
    {
        // Arrange — older JSONL format used a plain string for content
        var line = """{"type":"user","message":{"content":"talking about ksef today"}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.NotNull(match);
        Assert.Equal(SessionContentMatchSource.User, match!.Source);
    }

    [Fact]
    public void TryExtractMatch_MixedContentToolPlusText_ToolResultIgnored()
    {
        // Arrange — user message mixes a tool_result and a real text follow-up.
        // Tool_result mentions ksef but should not count; text does not mention it.
        var line = """{"type":"user","message":{"content":[{"type":"tool_result","content":"ksef in some grep output"},{"type":"text","text":"please continue"}]}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.Null(match);
    }

    [Fact]
    public void TryExtractMatch_CustomTitle_ReturnsCustomTitleMatch()
    {
        // Arrange — /rename custom title contains the query
        var line = """{"type":"custom-title","customTitle":"KSEF integration plan"}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.NotNull(match);
        Assert.Equal(SessionContentMatchSource.CustomTitle, match!.Source);
    }

    [Fact]
    public void TryExtractMatch_MalformedJson_ReturnsNull()
    {
        // Arrange
        var line = "not valid json with ksef in it";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.Null(match);
    }

    // --- Excerpt building ---

    [Fact]
    public void TryExtractMatch_LongText_ReturnsTrimmedExcerptWithEllipsis()
    {
        // Arrange — query in the middle of a long string
        var prefix = new string('a', 200);
        var suffix = new string('b', 200);
        var text = prefix + "ksef" + suffix;
        var line = "{\"type\":\"user\",\"message\":{\"content\":\"" + text + "\"}}";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.NotNull(match);
        Assert.Contains("ksef", match!.Excerpt);
        Assert.StartsWith("...", match.Excerpt);
        Assert.EndsWith("...", match.Excerpt);
        // Excerpt should be much shorter than the original
        Assert.True(match.Excerpt.Length < text.Length / 2,
            $"Expected trimmed excerpt, got {match.Excerpt.Length} chars from {text.Length}");
    }

    [Fact]
    public void TryExtractMatch_ShortText_ReturnsExcerptWithoutEllipsis()
    {
        // Arrange — text shorter than excerpt window
        var line = """{"type":"user","message":{"content":"short text with ksef"}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.NotNull(match);
        Assert.False(match!.Excerpt.StartsWith("..."));
        Assert.False(match.Excerpt.EndsWith("..."));
    }

    [Fact]
    public void TryExtractMatch_NewlinesInText_CollapsedInExcerpt()
    {
        // Arrange — multi-line text shouldn't break tooltip
        var line = """{"type":"user","message":{"content":"line one\nline with ksef\nline three"}}""";

        // Act
        var match = SessionContentSearchService.TryExtractMatch(line, "ksef");

        // Assert
        Assert.NotNull(match);
        Assert.DoesNotContain('\n', match!.Excerpt);
        Assert.DoesNotContain('\r', match.Excerpt);
    }
}
