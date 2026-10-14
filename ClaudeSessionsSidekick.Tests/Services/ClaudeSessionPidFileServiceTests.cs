using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick.Tests.Services;

public class ClaudeSessionPidFileServiceTests
{
    [Fact]
    public void Parse_ReadsAllFields()
    {
        // Arrange — shape of a real ~/.claude/sessions/<pid>.json
        var json = """
            {"pid":25504,"sessionId":"7d3e0b3d-167d-45ef-a55e-d2114f378e15",
             "cwd":"D:\\XGGProjectsGit\\ExergyERP_Dev","startedAt":1791986391091,
             "name":"fixing KSeF microservice","nameSource":"user",
             "updatedAt":1791986469050,"status":"busy"}
            """;

        // Act
        var info = ClaudeSessionPidFileService.Parse(json, expectedPid: 25504);

        // Assert
        Assert.NotNull(info);
        Assert.Equal(25504, info!.Pid);
        Assert.Equal("7d3e0b3d-167d-45ef-a55e-d2114f378e15", info.SessionId);
        Assert.Equal(@"D:\XGGProjectsGit\ExergyERP_Dev", info.Cwd);
        Assert.Equal("fixing KSeF microservice", info.Name);
        Assert.Equal("busy", info.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791986469050), info.UpdatedAt);
    }

    [Fact]
    public void Parse_PidMismatch_ReturnsNull()
    {
        // Arrange — a stale file left behind by a reused process ID.
        var json = @"{""pid"":111,""sessionId"":""7d3e0b3d-167d-45ef-a55e-d2114f378e15""}";

        // Act
        var info = ClaudeSessionPidFileService.Parse(json, expectedPid: 222);

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public void Parse_SessionIdNotAGuid_ReturnsNull()
    {
        // Arrange
        var json = @"{""pid"":111,""sessionId"":""not-a-guid""}";

        // Act
        var info = ClaudeSessionPidFileService.Parse(json, expectedPid: 111);

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public void Parse_BrokenJson_ReturnsNull()
    {
        // Arrange
        var json = @"{""pid"":111,";

        // Act
        var info = ClaudeSessionPidFileService.Parse(json, expectedPid: 111);

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public void Parse_MissingOptionalFields_StillReturnsInfo()
    {
        // Arrange — minimal file: only pid + sessionId.
        var json = @"{""pid"":111,""sessionId"":""7d3e0b3d-167d-45ef-a55e-d2114f378e15""}";

        // Act
        var info = ClaudeSessionPidFileService.Parse(json, expectedPid: 111);

        // Assert
        Assert.NotNull(info);
        Assert.Null(info!.Cwd);
        Assert.Null(info.Name);
        Assert.Null(info.Status);
    }

    [Fact]
    public void Parse_MissingPid_ReturnsNull()
    {
        // Arrange — Claude Code always writes the pid; a file without it is
        // not a valid process record.
        var json = @"{""sessionId"":""7d3e0b3d-167d-45ef-a55e-d2114f378e15""}";

        // Act
        var info = ClaudeSessionPidFileService.Parse(json, expectedPid: 111);

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public void Parse_NameWithNewlinesAndExcessLength_IsSanitized()
    {
        // Arrange — a hand-edited file must not flood the log or break lines.
        var longName = new string('x', 300);
        var json = $@"{{""pid"":111,""sessionId"":""7d3e0b3d-167d-45ef-a55e-d2114f378e15"",""name"":""bad\r\nline {longName}""}}";

        // Act
        var info = ClaudeSessionPidFileService.Parse(json, expectedPid: 111);

        // Assert
        Assert.NotNull(info);
        Assert.DoesNotContain('\n', info!.Name!);
        Assert.DoesNotContain('\r', info.Name!);
        Assert.Equal(200, info.Name!.Length);
    }
}
