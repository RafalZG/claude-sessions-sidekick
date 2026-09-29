using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Result of matching running Claude processes to tracked sessions: the
/// sessions confirmed open, plus the processes we could not map to any
/// session (the caller may try other lookups for those).
/// </summary>
public sealed record OpenSessionMatchResult(
    List<SessionTokenData> Matched
    , List<FocusedClaudeProcess> Unmatched);
