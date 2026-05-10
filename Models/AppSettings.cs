using System.Text.Json.Serialization;

namespace ClaudeSessionsSidekick.Models;

public class AppSettings
{
    [JsonPropertyName("quickLaunchEntries")]
    public List<QuickLaunchEntry> QuickLaunchEntries { get; set; } = [];

    [JsonPropertyName("enableMainHotkey")]
    public bool EnableMainHotkey { get; set; } = true;

    [JsonPropertyName("widgetToggleHotkey")]
    public string WidgetToggleHotkey { get; set; } = "Win+LAlt+C";

    [JsonPropertyName("sessionBrowserHotkey")]
    public string SessionBrowserHotkey { get; set; } = "Win+LAlt+S";

    [JsonPropertyName("promptLibraryHotkey")]
    public string PromptLibraryHotkey { get; set; } = "Win+LAlt+P";

    [JsonPropertyName("permissionManagerHotkey")]
    public string PermissionManagerHotkey { get; set; } = "Win+LAlt+N";

    [JsonPropertyName("claudeConfigHotkey")]
    public string ClaudeConfigHotkey { get; set; } = "Win+LAlt+K";

    [JsonPropertyName("agentsSkillsHotkey")]
    public string AgentsSkillsHotkey { get; set; } = "Win+LAlt+A";

    [JsonPropertyName("compactAggressiveness")]
    public CompactAggressiveness CompactAggressiveness { get; set; } = CompactAggressiveness.Balanced;

    [JsonPropertyName("enableCompactNotifications")]
    public bool EnableCompactNotifications { get; set; } = true;

    [JsonPropertyName("enablePermissionSuggestions")]
    public bool EnablePermissionSuggestions { get; set; } = true;

    [JsonPropertyName("showActiveSessions")]
    public bool ShowActiveSessions { get; set; } = true;

    [JsonPropertyName("customCriticalPercent")]
    public int CustomCriticalPercent { get; set; } = 75;

    [JsonPropertyName("customWarningPercent")]
    public int CustomWarningPercent { get; set; } = 50;

    [JsonPropertyName("widgetViewMode")]
    public WidgetViewMode WidgetViewMode { get; set; } = WidgetViewMode.Compact;

    [JsonPropertyName("preferredShell")]
    public ShellType PreferredShell { get; set; } = ShellType.Auto;

    [JsonPropertyName("claudeExePath")]
    public string? ClaudeExePath { get; set; }

    [JsonPropertyName("claudeHomeDir")]
    public string? ClaudeHomeDir { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShellType
{
    Auto,
    Cmd,
    PowerShell,
    GitBash
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompactAggressiveness
{
    Conservative,
    Balanced,
    Aggressive,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetViewMode
{
    Full,
    Compact,
    Mini
}
