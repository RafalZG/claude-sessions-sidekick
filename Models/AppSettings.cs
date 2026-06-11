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

    /// <summary>
    /// Master switch for the automatic update check on startup. When off, no
    /// background poll runs and no tray balloon shows — the user can still
    /// trigger a check manually via the tray menu, which bypasses both this
    /// switch and the 8h throttle below.
    /// </summary>
    [JsonPropertyName("checkForUpdatesOnStartup")]
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// Timestamp of the last automatic update check (UTC). Used to throttle
    /// the auto-check so that quickly-restarting users (e.g. when several
    /// updates ship the same day) don't get pestered with a fresh toast
    /// every restart. Manual checks via the tray menu don't update this.
    /// </summary>
    [JsonPropertyName("lastUpdateCheckUtc")]
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

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

    /// <summary>
    /// Alias (sonnet/opus/haiku) or full ID forced via <c>--model</c> on every
    /// resume from the Session Browser. Lets users park older sessions on a
    /// newer model without having to <c>/model</c> after every resume.
    /// Null = no override (Claude continues with whatever was last selected).
    /// </summary>
    [JsonPropertyName("resumeDefaultModel")]
    public string? ResumeDefaultModel { get; set; }
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
