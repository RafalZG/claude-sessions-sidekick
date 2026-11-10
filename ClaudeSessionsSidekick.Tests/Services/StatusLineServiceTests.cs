using System.Text.Json.Nodes;
using ClaudeSessionsSidekick.Services;
using Xunit;

namespace ClaudeSessionsSidekick.Tests.Services;

/// <summary>
/// All tests pass explicit settingsPath/scriptPath into a per-test temp folder,
/// so nothing here ever touches the real ~/.claude/settings.json.
/// </summary>
public class StatusLineServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;
    private readonly string _scriptPath;

    public StatusLineServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "SidekickStatusLineTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "settings.json");
        _scriptPath = Path.Combine(_dir, "statusline.ps1");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public void BuildCommand_UsesForwardSlashesAndAppendsStamp()
    {
        // Act — Git Bash on Windows eats unquoted backslashes, so the
        // command must come out with forward slashes; the stamp makes the
        // command string unique per Apply so open sessions refresh at once.
        var command = StatusLineService.BuildCommand(@"C:\Users\x\AppData\Roaming\statusline.ps1", "20261110013000");

        // Assert
        Assert.Equal(
            "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:/Users/x/AppData/Roaming/statusline.ps1\" 20261110013000",
            command);
    }

    [Fact]
    public void Apply_Twice_ProducesDifferentCommandStamps_WhenClockAdvances()
    {
        // Arrange + Act — the stamp has 1s resolution; what matters for the
        // immediate-refresh trigger is that the command still points at our
        // script and carries a trailing stamp argument.
        StatusLineService.Apply("{project}", _settingsPath, _scriptPath);
        var status = StatusLineService.GetStatus(_settingsPath, _scriptPath);

        // Assert
        Assert.Equal(StatusLineState.Ours, status.State);
        Assert.Matches(@" \d{14}$", status.CurrentCommand);
    }

    [Fact]
    public void Apply_NoSettingsFile_CreatesSettingsAndScript()
    {
        // Act
        StatusLineService.Apply("{project} {ctx}%", _settingsPath, _scriptPath);

        // Assert
        Assert.True(File.Exists(_scriptPath));
        var root = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject;
        Assert.NotNull(root);
        var statusLine = root!["statusLine"] as JsonObject;
        Assert.NotNull(statusLine);
        Assert.Equal("command", statusLine!["type"]!.GetValue<string>());
        Assert.Contains("statusline.ps1", statusLine["command"]!.GetValue<string>());
    }

    [Fact]
    public void Apply_WritesScriptAsUtf8WithBom()
    {
        // Windows PowerShell 5.1 decodes a BOM-less script in the ANSI
        // codepage — non-ASCII template text would garble and a C1 byte
        // could even decode into a string-ending quote.
        StatusLineService.Apply("{project} zażółć", _settingsPath, _scriptPath);

        var bytes = File.ReadAllBytes(_scriptPath);
        Assert.True(bytes.Length > 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public void GetStatus_CorruptSettingsFile_ReportsUnreadable()
    {
        File.WriteAllText(_settingsPath, "{ not json");

        var status = StatusLineService.GetStatus(_settingsPath, _scriptPath);

        Assert.Equal(StatusLineState.Unreadable, status.State);
    }

    [Fact]
    public void Apply_PreservesOtherSettingsKeys()
    {
        // Arrange — a settings.json with keys Sidekick knows nothing about.
        File.WriteAllText(_settingsPath,
            """{ "model": "opus", "permissions": { "allow": ["Bash(git status)"] } }""");

        // Act
        StatusLineService.Apply("{project}", _settingsPath, _scriptPath);

        // Assert
        var root = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject;
        Assert.Equal("opus", root!["model"]!.GetValue<string>());
        Assert.NotNull(root["permissions"]);
        Assert.NotNull(root["statusLine"]);
    }

    [Fact]
    public void Apply_CorruptSettingsFile_ThrowsAndLeavesFileUntouched()
    {
        // Arrange — writing over a file we couldn't parse would wipe the
        // user's whole configuration; it must abort instead.
        var corrupt = "{ not json at all";
        File.WriteAllText(_settingsPath, corrupt);

        // Act + Assert
        Assert.ThrowsAny<Exception>(() =>
            StatusLineService.Apply("{project}", _settingsPath, _scriptPath));
        Assert.Equal(corrupt, File.ReadAllText(_settingsPath));
        Assert.False(File.Exists(_scriptPath));
    }

    [Fact]
    public void Apply_ExistingFile_WritesBakBackup()
    {
        // Arrange
        File.WriteAllText(_settingsPath, """{ "model": "opus" }""");

        // Act
        StatusLineService.Apply("{project}", _settingsPath, _scriptPath);

        // Assert
        Assert.True(File.Exists(_settingsPath + ".bak"));
        var backup = JsonNode.Parse(File.ReadAllText(_settingsPath + ".bak")) as JsonObject;
        Assert.Null(backup!["statusLine"]);
    }

    [Fact]
    public void GetStatus_ReportsNotSet_Ours_Foreign()
    {
        // Not set
        var before = StatusLineService.GetStatus(_settingsPath, _scriptPath);
        Assert.Equal(StatusLineState.NotSet, before.State);

        // Ours
        StatusLineService.Apply("{project}", _settingsPath, _scriptPath);
        var ours = StatusLineService.GetStatus(_settingsPath, _scriptPath);
        Assert.Equal(StatusLineState.Ours, ours.State);

        // Foreign
        var root = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject;
        var foreignEntry = new JsonObject
        {
            ["type"] = "command",
            ["command"] = "~/.claude/my-own-statusline.sh"
        };
        root!["statusLine"] = foreignEntry;
        File.WriteAllText(_settingsPath, root.ToJsonString());
        var foreign = StatusLineService.GetStatus(_settingsPath, _scriptPath);
        Assert.Equal(StatusLineState.Foreign, foreign.State);
        Assert.Equal("~/.claude/my-own-statusline.sh", foreign.CurrentCommand);
    }

    [Fact]
    public void Remove_DeletesKeyAndScript_KeepsOtherSettings()
    {
        // Arrange
        File.WriteAllText(_settingsPath, """{ "model": "opus" }""");
        StatusLineService.Apply("{project}", _settingsPath, _scriptPath);

        // Act
        StatusLineService.Remove(_settingsPath, _scriptPath);

        // Assert
        var root = JsonNode.Parse(File.ReadAllText(_settingsPath)) as JsonObject;
        Assert.Null(root!["statusLine"]);
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.False(File.Exists(_scriptPath));
        var after = StatusLineService.GetStatus(_settingsPath, _scriptPath);
        Assert.Equal(StatusLineState.NotSet, after.State);
    }

    [Fact]
    public void Remove_NothingConfigured_DoesNotCreateSettingsFile()
    {
        // Act
        StatusLineService.Remove(_settingsPath, _scriptPath);

        // Assert
        Assert.False(File.Exists(_settingsPath));
    }
}
