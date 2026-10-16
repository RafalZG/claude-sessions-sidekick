namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// One entry of <c>~/.claude/sessions/&lt;pid&gt;.json</c> — Claude Code's own
/// record of which session a running process is showing. The session ID here
/// is exact and current, which beats every heuristic (resume argument,
/// working directory, window title) we otherwise fall back to.
/// </summary>
public sealed record ClaudeSessionPidInfo(
    int Pid
    , string SessionId
    , string? Cwd
    , string? Name
    , string? NameSource
    , string? Status
    , DateTimeOffset UpdatedAt)
{
    /// <summary>True only for a name the user typed themselves (/rename).
    /// Claude's own "derived" names are throwaway labels like "dev-63" and
    /// must never be shown as the session's name.</summary>
    public bool HasUserName => NameSource == "user" && !string.IsNullOrWhiteSpace(Name);
}
