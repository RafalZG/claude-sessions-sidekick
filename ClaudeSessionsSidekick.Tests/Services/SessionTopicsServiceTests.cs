using System.IO;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

[Collection("SessionTopicsService")]
public class SessionTopicsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public SessionTopicsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TopicsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "session-topics.json");
        SessionTopicsService.UseStoragePathForTesting(_path);
    }

    public void Dispose()
    {
        SessionTopicsService.UseStoragePathForTesting(null);
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetTopic_NoTopicSet_ReturnsNull()
    {
        Assert.Null(SessionTopicsService.GetTopic("missing-session"));
    }

    [Fact]
    public void SetTopic_ThenGet_ReturnsStoredText()
    {
        SessionTopicsService.SetTopic("s1", "Cash invoice debugging");
        Assert.Equal("Cash invoice debugging", SessionTopicsService.GetTopic("s1"));
    }

    [Fact]
    public void SetTopic_TrimsWhitespace()
    {
        SessionTopicsService.SetTopic("s1", "   trimmed   ");
        Assert.Equal("trimmed", SessionTopicsService.GetTopic("s1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SetTopic_EmptyOrWhitespace_ClearsOverride(string? value)
    {
        SessionTopicsService.SetTopic("s1", "old topic");
        SessionTopicsService.SetTopic("s1", value);
        Assert.Null(SessionTopicsService.GetTopic("s1"));
    }

    [Fact]
    public void SetTopic_OverwritesExisting()
    {
        SessionTopicsService.SetTopic("s1", "first");
        SessionTopicsService.SetTopic("s1", "second");
        Assert.Equal("second", SessionTopicsService.GetTopic("s1"));
    }

    [Fact]
    public void SetTopic_PersistsToDisk()
    {
        SessionTopicsService.SetTopic("s1", "persisted");
        // Force a fresh load
        SessionTopicsService.UseStoragePathForTesting(_path);
        Assert.Equal("persisted", SessionTopicsService.GetTopic("s1"));
    }

    [Fact]
    public void SetTopic_MultipleSessions_KeptIndependent()
    {
        SessionTopicsService.SetTopic("s1", "first session");
        SessionTopicsService.SetTopic("s2", "second session");
        Assert.Equal("first session", SessionTopicsService.GetTopic("s1"));
        Assert.Equal("second session", SessionTopicsService.GetTopic("s2"));
    }
}
