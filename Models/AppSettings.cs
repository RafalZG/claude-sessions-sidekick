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

    /// <summary>
    /// Copies Claude's latest reply (from the most recently active session) to the
    /// clipboard as clean, original text — no console width-wrapping or indentation.
    /// </summary>
    [JsonPropertyName("copyLatestReplyHotkey")]
    public string CopyLatestReplyHotkey { get; set; } = "LCtrl+LAlt+R";

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
    /// Effort level (low/medium/high/xhigh/max) forced via <c>--effort</c> on
    /// every resume from the Session Browser. Null = no override (Claude uses
    /// its own default — <c>high</c> on Opus 4.7+, Sonnet 4.6+, Fable 5).
    /// <c>ultracode</c> is deliberately not exposed here: it auto-spawns
    /// dynamic workflows for every substantive task and is too easy to leave
    /// on by accident, burning plan limits. Power users can still set it
    /// inside the session via <c>/effort ultracode</c>.
    /// </summary>
    [JsonPropertyName("resumeEffortLevel")]
    public string? ResumeEffortLevel { get; set; }

    /// <summary>
    /// Global hotkey that turns a clipboard screenshot into a file Claude Code
    /// can attach (save PNG → put on clipboard as a file-drop → synthesize
    /// Ctrl+V into the focused terminal). Works around the fact that native
    /// Windows terminals can't paste a raw clipboard bitmap into Claude Code.
    /// </summary>
    [JsonPropertyName("enableScreenshotPasteHotkey")]
    public bool EnableScreenshotPasteHotkey { get; set; } = true;

    [JsonPropertyName("screenshotPasteHotkey")]
    public string ScreenshotPasteHotkey { get; set; } = "LCtrl+LAlt+V";

    /// <summary>
    /// Folder for the saved PNGs. Null = %APPDATA%\ClaudeSessionsSidekick\Screenshots.
    /// </summary>
    [JsonPropertyName("screenshotSaveDir")]
    public string? ScreenshotSaveDir { get; set; }

    /// <summary>
    /// How many recent screenshots to keep on disk; older ones are pruned after
    /// each paste. 0 or less = keep everything.
    /// </summary>
    [JsonPropertyName("screenshotRetentionCount")]
    public int ScreenshotRetentionCount { get; set; } = 50;

    /// <summary>
    /// Periodically nudge the user to review + consolidate their Claude Code memory
    /// files (dedupe, drop stale entries, keep memory lean). The nudge only fires
    /// when the footprint is actually worth reviewing — see <see cref="MemoryReviewThresholdTokens"/>.
    /// </summary>
    [JsonPropertyName("enableMemoryReviewSuggestions")]
    public bool EnableMemoryReviewSuggestions { get; set; } = true;

    /// <summary>Minimum days between memory-review nudges.</summary>
    [JsonPropertyName("memoryReviewIntervalDays")]
    public int MemoryReviewIntervalDays { get; set; } = 7;

    /// <summary>
    /// Only nudge once the estimated memory footprint reaches this many tokens, so
    /// users with little or no memory aren't pestered.
    /// </summary>
    [JsonPropertyName("memoryReviewThresholdTokens")]
    public int MemoryReviewThresholdTokens { get; set; } = 25_000;

    /// <summary>Timestamp of the last memory-review nudge (UTC), for throttling.</summary>
    [JsonPropertyName("lastMemoryReviewSuggestionUtc")]
    public DateTimeOffset? LastMemoryReviewSuggestionUtc { get; set; }

    /// <summary>
    /// After a PC restart, offer to reopen the Claude Code sessions that were open
    /// before the restart (Chrome-style session restore).
    /// </summary>
    [JsonPropertyName("enableSessionRestore")]
    public bool EnableSessionRestore { get; set; } = true;
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
