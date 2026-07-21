using System.Text.Json.Serialization;

namespace ClaudeSessionsSidekick.Models;

/// <summary>One open session captured for potential restore after a reboot.</summary>
public class OpenSessionRef
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    /// <summary>Working directory to relaunch in (the session's Cwd).</summary>
    [JsonPropertyName("folderPath")]
    public string? FolderPath { get; set; }

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "";

    [JsonPropertyName("lastSeenUtc")]
    public DateTimeOffset LastSeenUtc { get; set; }
}

/// <summary>
/// A point-in-time snapshot of which Claude Code sessions were open, persisted so
/// that after a PC restart Sidekick can offer to reopen them (Chrome-style restore).
/// </summary>
public class OpenSessionSnapshot
{
    /// <summary>When this snapshot was written (UTC). Compared against system boot
    /// time to tell "reboot killed my open sessions" from "I closed them myself".</summary>
    [JsonPropertyName("capturedUtc")]
    public DateTimeOffset CapturedUtc { get; set; }

    [JsonPropertyName("sessions")]
    public List<OpenSessionRef> Sessions { get; set; } = [];
}
