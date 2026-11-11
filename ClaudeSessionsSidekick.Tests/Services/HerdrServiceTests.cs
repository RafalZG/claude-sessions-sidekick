using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class HerdrServiceTests
{
    [Fact]
    public void TryParsePaneId_RealTabCreateOutput_ReturnsPaneId()
    {
        // Captured verbatim from `herdr tab create` 0.9.5.
        var json = """
            {"id":"cli:tab:create","result":{"root_pane":{"agent_status":"unknown","cwd":"D:\\XGGProjectsGit\\ExergyERP_Dev\\","focused":false,"pane_id":"w1:p2","revision":0,"tab_id":"w1:t2","terminal_id":"term_65fef75f59a792","workspace_id":"w1"},"tab":{"label":"sidekick-poc","number":2,"pane_count":1,"tab_id":"w1:t2","workspace_id":"w1"},"type":"tab_created"}}
            """;

        var paneId = HerdrService.TryParsePaneId(json);

        Assert.Equal("w1:p2", paneId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"result":{}}""")]
    [InlineData("""{"result":{"root_pane":{}}}""")]
    [InlineData("""{"result":{"root_pane":{"pane_id":""}}}""")]
    [InlineData("not json at all")]
    public void TryParsePaneId_MissingOrMalformedShape_ReturnsNull(string json)
    {
        var paneId = HerdrService.TryParsePaneId(json);

        Assert.Null(paneId);
    }

    [Theory]
    [InlineData("w1:p2; rm -rf /")]
    [InlineData("w1 p2")]
    [InlineData("w1:p2\n")]
    public void TryParsePaneId_IdWithUnexpectedCharacters_ReturnsNull(string paneId)
    {
        var json = BuildTabCreateJson(paneId);

        var parsed = HerdrService.TryParsePaneId(json);

        Assert.Null(parsed);
    }

    [Fact]
    public void TryParsePaneId_IdLongerThan64Chars_ReturnsNull()
    {
        var longId = new string('a', 65);
        var json = BuildTabCreateJson(longId);

        var parsed = HerdrService.TryParsePaneId(json);

        Assert.Null(parsed);
    }

    private static string BuildTabCreateJson(string paneId)
    {
        var payload = new { result = new { root_pane = new { pane_id = paneId } } };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        return json;
    }

    [Theory]
    [InlineData("ExergyERP_Dev", "ExergyERP_Dev")]
    [InlineData("  my project  ", "my project")]
    [InlineData("wt-c3600 (kopia)", "wt-c3600 (kopia)")]
    [InlineData("ha$ck`er;name", "hackername")]
    [InlineData("", "claude")]
    [InlineData("   ", "claude")]
    [InlineData("!!!", "claude")]
    public void SanitizeLabel_KeepsSafeCharactersOnly(string input, string expected)
    {
        var label = HerdrService.SanitizeLabel(input);

        Assert.Equal(expected, label);
    }

    [Fact]
    public void SanitizeLabel_CapsLengthAt48()
    {
        var input = new string('a', 100);

        var label = HerdrService.SanitizeLabel(input);

        Assert.Equal(48, label.Length);
    }

    [Theory]
    [InlineData("ExergyERP_Dev")]
    [InlineData("3d-print")]
    [InlineData("Żółta Łąka")]
    [InlineData("---")]
    [InlineData("claude")]
    public void BuildAgentName_AlwaysMatchesHerdrNameFormat(string label)
    {
        var name = HerdrService.BuildAgentName(label);

        // Herdr requires [a-z][a-z0-9_-]{0,31} for live agent names.
        Assert.Matches("^[a-z][a-z0-9_-]{0,31}$", name);
    }

    [Fact]
    public void BuildAgentName_SameLabelRepeatedly_ProducesDistinctNames()
    {
        // The suffix is random (16 bits), so any single pair could collide;
        // five draws all landing on one value is beyond realistic flake odds.
        var names = Enumerable.Range(0, 5)
            .Select(_ => HerdrService.BuildAgentName("ExergyERP_Dev"))
            .ToList();

        var distinctCount = names.Distinct().Count();
        Assert.True(distinctCount > 1);
    }

    [Fact]
    public void SplitClaudeArgs_ResumeCommand_SplitsIntoTokens()
    {
        var tokens = HerdrService.SplitClaudeArgs(
            "claude --resume 522efa42-a6e6-4963-84d8-2fe60aa23c65 --model opus --effort high");

        var expected = new[]
        {
            "claude", "--resume", "522efa42-a6e6-4963-84d8-2fe60aa23c65",
            "--model", "opus", "--effort", "high"
        };
        Assert.Equal(expected, tokens);
    }

    [Fact]
    public void SplitClaudeArgs_PlainClaude_SingleToken()
    {
        var tokens = HerdrService.SplitClaudeArgs("claude");

        Assert.Equal(new[] { "claude" }, tokens);
    }
}
