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
    [InlineData("[]")]
    [InlineData("""{"result":"x"}""")]
    [InlineData("""{"result":{"root_pane":"x"}}""")]
    [InlineData("""{"result":{"root_pane":{"pane_id":5}}}""")]
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
    [InlineData("--help", "help")]
    [InlineData("-- --focus", "focus")]
    [InlineData("(old) app", "old) app")]
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

    [Fact]
    public void ParseOpenSessionIds_RealAgentListOutput_ReturnsOnlyAgentsWithSession()
    {
        // Shape captured from `herdr agent list` 0.9.5: two agents restored by
        // herdr carry agent_session, one started by Sidekick does not yet.
        var json = """
            {"id":"cli:agent:list","result":{"agents":[
              {"agent":"claude","agent_session":{"agent":"claude","kind":"id","source":"herdr:claude","value":"614cb2ed-3594-41bb-b7bf-fe6ee037144b"},"agent_status":"idle","pane_id":"w1:pF"},
              {"agent":"claude","agent_session":{"agent":"claude","kind":"id","source":"herdr:claude","value":"da058771-b874-4162-ab95-a539a5dc2bd2"},"agent_status":"idle","pane_id":"w1:pG"},
              {"agent":"claude","agent_status":"idle","pane_id":"w1:pH"}
            ]}}
            """;

        var ids = HerdrService.ParseOpenSessionIds(json);

        Assert.Equal(2, ids.Count);
        Assert.Contains("614CB2ED-3594-41BB-B7BF-FE6EE037144B", ids);
        Assert.Contains("da058771-b874-4162-ab95-a539a5dc2bd2", ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"error":{"code":"timeout"}}""")]
    [InlineData("""{"result":{"agents":"oops"}}""")]
    [InlineData("[]")]
    [InlineData("""{"result":"x"}""")]
    [InlineData("""{"result":{"agents":[{"agent_session":{"value":"not-a-guid"}},{"agent_session":{"value":5}},"x"]}}""")]
    public void ParseOpenSessionIds_MalformedOrNoSessions_ReturnsEmpty(string json)
    {
        var ids = HerdrService.ParseOpenSessionIds(json);

        Assert.Empty(ids);
    }

    [Theory]
    [InlineData("\"C:\\Users\\x\\.herdr\\packages\\standalone\\releases\\0.9.5\\herdr.exe\" ", true)]
    [InlineData("\"C:\\Program Files\\Herdr\\herdr.exe\"", true)]
    [InlineData("herdr.exe", true)]
    [InlineData("herdr.exe --session work", true)]
    [InlineData("herdr.exe session attach work", true)]
    [InlineData("\"C:\\Users\\x\\.herdr\\herdr.exe\" server", false)]
    [InlineData("herdr.exe tab create --cwd D:\\x --label x --focus", false)]
    [InlineData("herdr.exe agent start x --kind claude --pane w1:p2 --", false)]
    [InlineData("herdr.exe session list", false)]
    [InlineData("C:\\Program Files\\Herdr\\herdr.exe", true)]
    [InlineData("C:\\Program Files\\Herdr\\herdr.exe server", false)]
    [InlineData("herdr.exe --session=work", true)]
    [InlineData("herdr.exe\tserver", false)]
    [InlineData("herdr.exe --remote me@box", true)]
    [InlineData("\"C:\\Users\\joe.exeter\\herdr.exe\" server", false)]
    [InlineData("\"C:\\Users\\joe.exeter\\herdr.exe\"", true)]
    public void IsHerdrClientCommandLine_RecognizesWindowsOnly(string commandLine, bool expected)
    {
        var isClient = HerdrService.IsHerdrClientCommandLine(commandLine);

        Assert.Equal(expected, isClient);
    }

    [Fact]
    public void ParseSavedSessionIds_RealSessionFileShape_FindsEverySavedSession()
    {
        // Trimmed from herdr 0.9.5 %APPDATA%\herdr\session.json: one plain
        // shell pane, two panes that held a claude agent.
        var json = """
            {"version":3,"workspaces":[{"id":"w1","tabs":[
              {"custom_name":"ExergyERP_Dev","panes":{"1":{"cwd":"D:\\x\\"}}},
              {"custom_name":"ExergyERP_Dev","panes":{"5":{"cwd":"D:\\x\\","agent_name":"exergyerp-dev-944e","managed_agent_kind":"claude",
                "agent_session":{"source":"herdr:claude","agent":"claude","kind":"id","value":"614cb2ed-3594-41bb-b7bf-fe6ee037144b"}}}},
              {"custom_name":"XggFramework_Dev","panes":{"6":{"cwd":"D:\\y\\",
                "agent_session":{"source":"herdr:claude","agent":"claude","kind":"id","value":"{DA058771-B874-4162-AB95-A539A5DC2BD2}"}}}}
            ]}]}
            """;

        var ids = HerdrService.ParseSavedSessionIds(json);

        Assert.Equal(2, ids.Count);
        Assert.Contains("614cb2ed-3594-41bb-b7bf-fe6ee037144b", ids);
        Assert.Contains("da058771-b874-4162-ab95-a539a5dc2bd2", ids);
        Assert.DoesNotContain("{DA058771-B874-4162-AB95-A539A5DC2BD2}", ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"version\":3")]
    [InlineData("""{"version":3,"workspaces":[]}""")]
    public void ParseSavedSessionIds_EmptyOrBroken_ReturnsEmpty(string json)
    {
        var ids = HerdrService.ParseSavedSessionIds(json);

        Assert.Empty(ids);
    }

    // Shape captured from `herdr workspace list` 0.9.5, with a second workspace added.
    private const string WorkspaceListJson = """
        {"id":"cli:workspace:list","result":{"type":"workspace_list","workspaces":[
          {"active_tab_id":"w1:tR","agent_status":"working","focused":true,"label":"XggFramework_Dev","number":1,"pane_count":7,"tab_count":7,"workspace_id":"w1"},
          {"active_tab_id":"w2:t1","agent_status":"idle","focused":false,"label":"ExergyERP_Dev","number":2,"pane_count":1,"tab_count":1,"workspace_id":"w2"}
        ]}}
        """;

    [Theory]
    [InlineData("ExergyERP_Dev", "w2")]
    [InlineData("xggframework_dev", "w1")]
    [InlineData("OtherProject", null)]
    public void TryFindWorkspaceIdByLabel_RealListOutput_MatchesByName(string label, string? expected)
    {
        var read = HerdrService.TryFindWorkspaceIdByLabel(WorkspaceListJson, label, out var workspaceId);

        Assert.True(read);
        Assert.Equal(expected, workspaceId);
    }

    [Fact]
    public void TryFindWorkspaceIdByLabel_EmptyList_ReadWithNoMatch()
    {
        var read = HerdrService.TryFindWorkspaceIdByLabel(
            """{"result":{"workspaces":[]}}""", "App", out var workspaceId);

        Assert.True(read);
        Assert.Null(workspaceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"result":{"workspaces":"oops"}}""")]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("""{"result":"x"}""")]
    [InlineData("""{"result":{"workspaces":[{"label":"App","workspace_id":"--version"}]}}""")]
    [InlineData("""{"result":{"workspaces":[{"label":"App","workspace_id":"w1 x"}]}}""")]
    [InlineData("""{"result":{"workspaces":[{"label":"App","workspace_id":5}]}}""")]
    public void TryFindWorkspaceIdByLabel_MalformedOrUnsafe_NotRead(string json)
    {
        var read = HerdrService.TryFindWorkspaceIdByLabel(json, "App", out var workspaceId);

        Assert.False(read);
        Assert.Null(workspaceId);
    }

    [Fact]
    public void TryParsePaneId_WorkspaceCreateShape_ReturnsRootPane()
    {
        var json = """
            {"id":"cli:workspace:create","result":{"workspace":{"workspace_id":"w2","label":"ExergyERP_Dev"},"tab":{"tab_id":"w2:t1"},"root_pane":{"pane_id":"w2:p1"}}}
            """;

        var paneId = HerdrService.TryParsePaneId(json);

        Assert.Equal("w2:p1", paneId);
    }

    [Fact]
    public void TryParseTabId_RealWorkspaceCreateOutput_ReturnsFirstTab()
    {
        // Trimmed from a real `herdr workspace create` on 0.9.5.
        var json = """
            {"id":"cli:workspace:create","result":{"root_pane":{"pane_id":"w2:p1","tab_id":"w2:t1","workspace_id":"w2"},"tab":{"label":"1","tab_id":"w2:t1"},"type":"workspace_created","workspace":{"label":"sidekick-test-a","workspace_id":"w2"}}}
            """;

        var tabId = HerdrService.TryParseTabId(json);

        Assert.Equal("w2:t1", tabId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"result":{"root_pane":"x"}}""")]
    [InlineData("""{"result":{"root_pane":{"tab_id":"--help"}}}""")]
    public void TryParseTabId_MalformedOrUnsafe_ReturnsNull(string json)
    {
        var tabId = HerdrService.TryParseTabId(json);

        Assert.Null(tabId);
    }

    [Fact]
    public void ExtractResumeSessionId_HerdrLaunchCommand_FindsId()
    {
        var id = FocusedSessionResolver.ExtractResumeSessionId(
            "claude --resume 614cb2ed-3594-41bb-b7bf-fe6ee037144b --model opus");

        Assert.Equal("614cb2ed-3594-41bb-b7bf-fe6ee037144b", id);
    }
}
