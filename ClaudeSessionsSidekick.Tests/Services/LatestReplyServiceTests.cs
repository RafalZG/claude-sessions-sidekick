using System;
using System.IO;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

public class LatestReplyServiceTests
{
    private static string Assistant(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { role = "assistant", content = new[] { new { type = "text", text } } },
        });

    private static string AssistantMultiBlock(string a, string b) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant",
            message = new { content = new[] { new { type = "text", text = a }, new { type = "text", text = b } } },
        });

    private const string ToolUseLine =
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"x","name":"Bash","input":{}}]}}""";
    private const string UserLine =
        """{"type":"user","message":{"content":[{"type":"text","text":"hello"}]}}""";

    [Fact]
    public void TryExtract_AssistantText_ReturnsText()
    {
        Assert.Equal("Hello there", LatestReplyService.TryExtractAssistantText(Assistant("Hello there")));
    }

    [Fact]
    public void TryExtract_PreservesOriginalNewlines()
    {
        // Original authored text with paragraph breaks — must survive verbatim
        // (that's the whole point vs. console-wrapped copy).
        var original = "Para one line one\nline two\n\nPara two.";
        Assert.Equal(original, LatestReplyService.TryExtractAssistantText(Assistant(original)));
    }

    [Fact]
    public void TryExtract_JoinsMultipleTextBlocksWithBlankLine()
    {
        Assert.Equal("first\n\nsecond", LatestReplyService.TryExtractAssistantText(AssistantMultiBlock("first", "second")));
    }

    [Fact]
    public void TryExtract_ToolUseOnly_ReturnsNull()
    {
        Assert.Null(LatestReplyService.TryExtractAssistantText(ToolUseLine));
    }

    [Fact]
    public void TryExtract_UserMessage_ReturnsNull()
    {
        Assert.Null(LatestReplyService.TryExtractAssistantText(UserLine));
    }

    [Fact]
    public void TryExtract_Garbage_ReturnsNull()
    {
        Assert.Null(LatestReplyService.TryExtractAssistantText("not json"));
        Assert.Null(LatestReplyService.TryExtractAssistantText(""));
    }

    [Fact]
    public void ExtractLatest_ReturnsLastAssistantTextSkippingTrailingToolUse()
    {
        var lines = new[]
        {
            UserLine,
            Assistant("first reply"),
            UserLine,
            Assistant("THE LATEST REPLY"),
            ToolUseLine, // trailing tool_use after the reply must be skipped
        };
        var path = Path.Combine(Path.GetTempPath(), "reply-test-" + Guid.NewGuid() + ".jsonl");
        File.WriteAllLines(path, lines);
        try
        {
            Assert.Equal("THE LATEST REPLY", LatestReplyService.ExtractLatestAssistantText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractLatest_MissingFile_ReturnsNull()
    {
        Assert.Null(LatestReplyService.ExtractLatestAssistantText(
            Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid() + ".jsonl")));
    }
}
