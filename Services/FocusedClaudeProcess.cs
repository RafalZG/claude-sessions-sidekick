namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// A running Claude Code CLI process that sits under the focused window.
/// Carries the two facts we can read from the outside: its command line
/// (which may name the exact session via <c>--resume</c>) and its current
/// working directory (which maps to a project's sessions).
/// </summary>
public sealed record FocusedClaudeProcess(int Pid, string CommandLine, string? WorkingDirectory);
