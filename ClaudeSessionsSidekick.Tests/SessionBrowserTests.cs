using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests;

public class SessionBrowserTests
{
    // --- DecodeProjectKey (pure logic, no filesystem) ---

    [Theory]
    [InlineData("C--src-Dev", @"C:\src\Dev")]
    [InlineData("D--Projects-MyApp-Main", @"D:\Projects\MyApp\Main")]
    [InlineData("C--Users-firstname.lastname.MONITOR", @"C:\Users\firstname.lastname.MONITOR")]
    [InlineData("E--work", @"E:\work")]
    public void DecodeProjectKey_DecodesValidKeys(string key, string expected)
    {
        var result = SessionBrowserWindow.DecodeProjectKey(key);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("X-only-single-dash")]
    public void DecodeProjectKey_ReturnsNull_ForInvalidKeys(string? key)
    {
        var result = SessionBrowserWindow.DecodeProjectKey(key);
        Assert.Null(result);
    }

    [Fact]
    public void DecodeProjectKey_DriveLetterOnly()
    {
        // "C--" = "C:\"
        var result = SessionBrowserWindow.DecodeProjectKey("C--");
        Assert.Equal(@"C:\", result);
    }

    // --- ProjectKeyToPath (with filesystem check) ---

    [Fact]
    public void ProjectKeyToPath_ReturnsNull_ForEmptyInput()
    {
        var result = SessionBrowserWindow.ProjectKeyToPath("");
        Assert.Null(result);
    }

    [Fact]
    public void ProjectKeyToPath_ReturnsNull_ForNonExistentPath()
    {
        // Valid key format but path won't exist
        var result = SessionBrowserWindow.ProjectKeyToPath(
            @"C:\Users\test\.claude\projects\Z--nonexistent-path-12345\session.jsonl");
        Assert.Null(result);
    }

    // --- CWD first-wins (session resume must use project root, not subdirectory) ---

    [Fact]
    public void ProcessLine_CwdFirstWins_KeepsProjectRoot()
    {
        // Arrange — Claude Code changes cwd during session (cd into subdirs)
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\Users\test\.claude\projects\test-proj\session.jsonl";

        var line1 = """{"type":"user","sessionId":"s1","cwd":"D:\\ProjectRoot","message":{"role":"user","content":"hi"},"timestamp":"2026-04-20T10:00:00Z"}""";
        var line2 = """{"type":"user","sessionId":"s1","cwd":"D:\\ProjectRoot\\SubDir","message":{"role":"user","content":"check sub"},"timestamp":"2026-04-20T10:05:00Z"}""";

        // Act — parse two user messages with different cwd
        watcher.ProcessLine(line1, fakePath);
        watcher.ProcessLine(line2, fakePath);

        // Assert — first cwd (project root) is kept
        var sessions = watcher.GetRecentSessions(hours: 24 * 365);
        var session = Assert.Single(sessions);
        Assert.Equal(@"D:\ProjectRoot", session.Cwd);
    }

    [Fact]
    public void ProcessLine_CustomTitle_SetsCustomName()
    {
        // Arrange — Claude Code writes custom-title when user runs /rename
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\Users\test\.claude\projects\test-proj\session.jsonl";

        var userLine = """{"type":"user","sessionId":"s1","cwd":"D:\\Project","message":{"role":"user","content":"hello"},"timestamp":"2026-04-20T10:00:00Z"}""";
        var titleLine = """{"type":"custom-title","customTitle":"magazyny w POS","sessionId":"s1"}""";

        // Act
        watcher.ProcessLine(userLine, fakePath);
        watcher.ProcessLine(titleLine, fakePath);

        // Assert
        var sessions = watcher.GetRecentSessions(hours: 24 * 365);
        var session = Assert.Single(sessions);
        Assert.Equal("magazyny w POS", session.CustomName);
        Assert.Equal("magazyny w POS", session.Topic);
    }

    [Fact]
    public void ProcessLine_CustomTitle_IsSearchableViaTopic()
    {
        // Arrange — verify that Topic (used by search filter) returns CustomName
        var watcher = new SessionWatcherService();
        var fakePath = @"C:\Users\test\.claude\projects\test-proj\session.jsonl";

        var userLine = """{"type":"user","sessionId":"s2","cwd":"D:\\Project","message":{"role":"user","content":"some unrelated first message"},"timestamp":"2026-04-20T10:00:00Z"}""";
        var titleLine = """{"type":"custom-title","customTitle":"magazyny w POS","sessionId":"s2"}""";

        // Act
        watcher.ProcessLine(userLine, fakePath);
        watcher.ProcessLine(titleLine, fakePath);

        // Assert — Topic prefers CustomName over FirstMessage
        var session = watcher.GetRecentSessions(hours: 24 * 365).Single();
        Assert.Equal("magazyny w POS", session.Topic);
        Assert.True(session.Topic.Contains("magazyny", StringComparison.OrdinalIgnoreCase));
    }

    // --- DecodeProjectKey known limitations ---

    [Fact]
    public void DecodeProjectKey_UnderscoreEncodedAsDash_CannotDistinguish()
    {
        // Claude Code encodes underscores as dashes in project keys.
        // "D:\Projects\MyApp_Dev" becomes "D--Projects-MyApp-Dev"
        // DecodeProjectKey cannot recover the underscore — it returns the wrong path.
        // The caller (ResumeSelected) falls back to vm.Cwd which has the correct path.
        var result = SessionBrowserWindow.DecodeProjectKey("D--Projects-MyApp-Dev");

        // Decoded path replaces all dashes with backslashes — won't match the real folder
        Assert.NotNull(result);
        Assert.Equal(@"D:\Projects\MyApp\Dev", result); // wrong but expected
        // ProjectKeyToPath would return null (Directory.Exists fails), triggering Cwd fallback
    }
}
