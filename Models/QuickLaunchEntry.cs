using System.Text.Json.Serialization;

namespace ClaudeSessionsSidekick.Models;

public class QuickLaunchEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("folderPath")]
    public string FolderPath { get; set; } = "";

    /// <summary>
    /// Full hotkey string, e.g. "Win+Alt+F", "Ctrl+Shift+B". Null = no hotkey.
    /// </summary>
    [JsonPropertyName("hotkey")]
    public string? Hotkey { get; set; }

    [JsonPropertyName("continueLastSession")]
    public bool ContinueLastSession { get; set; }

    [JsonPropertyName("shellOverride")]
    public ShellType? ShellOverride { get; set; }

    /// <summary>
    /// Forces a specific Claude model on every launch via the <c>--model</c>
    /// CLI flag — overrides whatever the project's settings.json or the last
    /// in-session <c>/model</c> choice would otherwise produce. Null = leave
    /// model selection to Claude Code's defaults.
    /// </summary>
    [JsonPropertyName("modelOverride")]
    public string? ModelOverride { get; set; }
}
