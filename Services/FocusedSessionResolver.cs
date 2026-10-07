using System.Text.RegularExpressions;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Pure decision logic for "which session does the focused window show?".
/// Kept free of Win32 calls so it can be unit-tested; the process/window
/// scanning lives in <see cref="FocusedSessionService"/>. GitHub issues #2/#3.
/// </summary>
public static class FocusedSessionResolver
{
    // Session IDs are version-4 GUIDs, passed after --resume as a separate
    // argument or as --resume=<id>.
    private static readonly Regex ResumeIdRegex = new(
        @"--resume[= ""']+([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
        RegexOptions.Compiled);

    /// <summary>
    /// Picks the session the user is looking at, given the sessions we track
    /// and the Claude processes found under the foreground window.
    /// Returns null when nothing matches — the caller then falls back to the
    /// old "newest session overall" behavior.
    /// </summary>
    public static SessionTokenData? Resolve(
        IReadOnlyList<SessionTokenData> sessions
        , IReadOnlyList<FocusedClaudeProcess> claudeProcesses
        , string? windowTitle
        , Action<string>? trace = null)
    {
        if (sessions.Count == 0 || claudeProcesses.Count == 0)
        {
            trace?.Invoke($"FocusResolve: nothing to match (sessions={sessions.Count}, processes={claudeProcesses.Count})");
            return null;
        }

        var candidates = new List<SessionTokenData>();
        foreach (var proc in claudeProcesses)
        {
            var matched = MatchProcessToSession(sessions, proc);
            trace?.Invoke(matched == null
                ? $"FocusResolve: pid={proc.Pid} matched no tracked session"
                : $"FocusResolve: pid={proc.Pid} -> session={matched.SessionId} ({matched.CustomName ?? matched.Slug ?? matched.ProjectName}, lastSeen={matched.LastSeen:HH:mm:ss})");
            if (matched != null && !candidates.Contains(matched))
            {
                candidates.Add(matched);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            var only = candidates[0];
            return only;
        }

        // Several Claude sessions live under one window (e.g. Windows Terminal
        // tabs sharing a single process, or several WT windows which by default
        // ALL live in one WindowsTerminal.exe). The window title shows the
        // active tab's title, which usually contains the session's name — use
        // it to pick the one the user actually sees.
        var byTitle = NarrowByTitle(candidates, windowTitle);
        trace?.Invoke(byTitle.Count == candidates.Count
            ? $"FocusResolve: title narrowed nothing ({candidates.Count} candidates stay) -> newest wins"
            : $"FocusResolve: title narrowed {candidates.Count} -> {byTitle.Count}");
        var newest = byTitle
            .OrderByDescending(s => s.LastSeen)
            .First();
        return newest;
    }

    /// <summary>
    /// Maps one Claude process to a tracked session: an exact session ID from
    /// <c>--resume</c> wins; otherwise the process's working directory picks
    /// the newest session of that project.
    /// </summary>
    public static SessionTokenData? MatchProcessToSession(
        IReadOnlyList<SessionTokenData> sessions
        , FocusedClaudeProcess proc)
    {
        var resumeId = ExtractResumeSessionId(proc.CommandLine);
        if (resumeId != null)
        {
            var byId = sessions.FirstOrDefault(
                s => string.Equals(s.SessionId, resumeId, StringComparison.OrdinalIgnoreCase));
            if (byId != null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(proc.WorkingDirectory))
        {
            var procDir = NormalizePath(proc.WorkingDirectory);
            var byCwd = sessions
                .Where(s => !string.IsNullOrWhiteSpace(s.Cwd)
                    && string.Equals(NormalizePath(s.Cwd!), procDir, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.LastSeen)
                .FirstOrDefault();
            return byCwd;
        }

        return null;
    }

    /// <summary>
    /// Maps every running Claude process to the session it is showing, for
    /// the "which sessions are open right now?" snapshot. A process with
    /// <c>--resume</c> claims that exact session; the rest claim the newest
    /// unclaimed sessions of their working directory — one per process, so
    /// two terminals in the same folder count as two different sessions.
    /// Processes that match nothing are returned too, so the caller can try
    /// other lookups for them.
    /// </summary>
    public static OpenSessionMatchResult MatchOpenSessions(
        IReadOnlyList<SessionTokenData> sessions
        , IReadOnlyList<FocusedClaudeProcess> claudeProcesses)
    {
        var matched = new List<SessionTokenData>();
        var claimedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<FocusedClaudeProcess>();
        var byCwd = new Dictionary<string, List<FocusedClaudeProcess>>(StringComparer.OrdinalIgnoreCase);

        foreach (var proc in claudeProcesses)
        {
            var resumeId = ExtractResumeSessionId(proc.CommandLine);
            if (resumeId != null)
            {
                var byId = sessions.FirstOrDefault(
                    s => string.Equals(s.SessionId, resumeId, StringComparison.OrdinalIgnoreCase));
                if (byId != null)
                {
                    // Two terminals resuming the same ID are one session — the
                    // second claim is dropped on purpose, not sent to Unmatched.
                    if (claimedIds.Add(byId.SessionId))
                    {
                        matched.Add(byId);
                    }
                }
                else
                {
                    // The command line names an exact session we don't track
                    // (e.g. resumed after a long idle). Never let it steal the
                    // folder's newest session — hand it back with its ID intact
                    // so the caller can build the reference from disk.
                    unmatched.Add(proc);
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(proc.WorkingDirectory))
            {
                var key = NormalizePath(proc.WorkingDirectory);
                if (!byCwd.TryGetValue(key, out var group))
                {
                    group = [];
                    byCwd[key] = group;
                }
                group.Add(proc);
            }
            else
            {
                unmatched.Add(proc);
            }
        }

        foreach (var kv in byCwd)
        {
            var group = kv.Value;
            var candidates = sessions
                .Where(s => !string.IsNullOrWhiteSpace(s.Cwd)
                    && string.Equals(NormalizePath(s.Cwd!), kv.Key, StringComparison.OrdinalIgnoreCase)
                    && !claimedIds.Contains(s.SessionId))
                .OrderByDescending(s => s.LastSeen)
                .Take(group.Count)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (claimedIds.Add(candidate.SessionId))
                {
                    matched.Add(candidate);
                }
            }

            // More processes in this folder than known sessions — hand the
            // leftovers back so the caller can look them up elsewhere.
            for (var i = candidates.Count; i < group.Count; i++)
            {
                unmatched.Add(group[i]);
            }
        }

        var result = new OpenSessionMatchResult(matched, unmatched);
        return result;
    }

    /// <summary>
    /// Answers whether a process's session is among the tracked candidates.
    /// A process launched with <c>--resume</c> counts as tracked ONLY when
    /// that exact ID is present — the working-directory fallback must not
    /// hide the mismatch, or a disk lookup for the real session would be
    /// skipped whenever the same folder holds some other session.
    /// </summary>
    public static bool HasTrackedSession(
        IReadOnlyList<SessionTokenData> sessions
        , FocusedClaudeProcess proc)
    {
        var resumeId = ExtractResumeSessionId(proc.CommandLine);
        if (resumeId != null)
        {
            return sessions.Any(
                s => string.Equals(s.SessionId, resumeId, StringComparison.OrdinalIgnoreCase));
        }

        var matched = MatchProcessToSession(sessions, proc);
        return matched != null;
    }

    /// <summary>
    /// Pulls the session ID out of a <c>claude --resume {id}</c> command line,
    /// or null when the process was started without --resume.
    /// </summary>
    public static string? ExtractResumeSessionId(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return null;
        }

        var match = ResumeIdRegex.Match(commandLine);
        if (!match.Success)
        {
            return null;
        }

        var id = match.Groups[1].Value;
        return id;
    }

    /// <summary>
    /// Recognizes a Claude Code CLI process. Two install flavors exist:
    /// npm (node.exe running @anthropic-ai/claude-code/cli.js) and the native
    /// launcher (claude.exe, e.g. from winget). Electron helper processes of
    /// the Claude DESKTOP app are also named claude.exe but always carry a
    /// --type= argument, so those are excluded.
    /// </summary>
    public static bool IsClaudeCliProcess(string exeName, string? commandLine)
    {
        if (exeName.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
        {
            return commandLine != null
                && commandLine.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) >= 0
                && commandLine.IndexOf("cli.js", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        if (exeName.Equals("claude.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (commandLine == null)
            {
                return true;
            }

            // The desktop app installs under ...\AnthropicClaude\app-x.y.z\ and
            // its MAIN process carries no --type= — the executable path is what
            // gives it away (seen in MonitorMike's log inflating the session
            // count). Only the first token is checked, so an ARGUMENT that
            // happens to mention the folder can't exclude a real CLI.
            if (commandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            var exePath = FirstCommandLineToken(commandLine);
            return exePath.IndexOf("AnthropicClaude", StringComparison.OrdinalIgnoreCase) < 0;
        }

        return false;
    }

    /// <summary>The executable part of a command line: the quoted first token,
    /// or everything up to the first space when unquoted.</summary>
    private static string FirstCommandLineToken(string commandLine)
    {
        var trimmed = commandLine.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 0 ? trimmed[1..closing] : trimmed;
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    /// <summary>
    /// Walks the parent chain of <paramref name="pid"/> and answers whether
    /// <paramref name="ancestorPid"/> appears in it. Depth-limited so a stale
    /// snapshot with a parent-PID loop (Windows reuses PIDs) can't spin forever.
    /// </summary>
    public static bool IsAncestor(
        int ancestorPid
        , int pid
        , IReadOnlyDictionary<int, int> parentOf
        , int maxDepth = 32)
    {
        var current = pid;
        for (var depth = 0; depth < maxDepth; depth++)
        {
            if (!parentOf.TryGetValue(current, out var parent) || parent == current || parent == 0)
            {
                return false;
            }

            if (parent == ancestorPid)
            {
                return true;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Keeps only the candidates the window title points at. Two passes: an
    /// exact substring match on the session's names first; when that helps no
    /// one, a looser word-overlap score — the terminal title is often a task
    /// description written by Claude ("Review and investigate the flaky test")
    /// that contains no slug, but shares words with the session's first
    /// message. When neither pass helps, all candidates stay so the caller
    /// can still fall back to "newest of the matched".
    /// </summary>
    internal static List<SessionTokenData> NarrowByTitle(
        List<SessionTokenData> candidates
        , string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return candidates;
        }

        var narrowed = candidates
            .Where(s => TitleMentionsSession(windowTitle, s))
            .ToList();
        if (narrowed.Count > 0)
        {
            return narrowed;
        }

        var byOverlap = NarrowByWordOverlap(candidates, windowTitle);
        return byOverlap;
    }

    private static bool TitleMentionsSession(string windowTitle, SessionTokenData session)
    {
        var names = new[] { session.CustomName, session.Slug, session.ProjectName };
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name)
                && windowTitle.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scores every candidate by how many distinct title words appear in its
    /// names or first message, and keeps the candidates with the single best
    /// positive score. A tie (including everyone at zero) keeps them all.
    /// </summary>
    private static List<SessionTokenData> NarrowByWordOverlap(
        List<SessionTokenData> candidates
        , string windowTitle)
    {
        var titleWords = SplitWords(windowTitle);
        if (titleWords.Count == 0)
        {
            return candidates;
        }

        var scored = new List<(SessionTokenData Session, int Score)>();
        foreach (var candidate in candidates)
        {
            var text = string.Join(
                " "
                , new[] { candidate.CustomName, candidate.Slug, candidate.ProjectName, candidate.FirstMessage }
                    .Where(n => !string.IsNullOrWhiteSpace(n)));
            var candidateWords = SplitWords(text);
            var score = titleWords.Count(w => candidateWords.Contains(w));
            scored.Add((candidate, score));
        }

        var best = scored.Max(x => x.Score);
        if (best == 0)
        {
            return candidates;
        }

        var winners = scored
            .Where(x => x.Score == best)
            .Select(x => x.Session)
            .ToList();
        return winners;
    }

    /// <summary>Distinct lowercase words of 4+ letters/digits — short words
    /// ("the", "and", "fix") match everything and only add noise.</summary>
    private static HashSet<string> SplitWords(string text)
    {
        var words = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+")
            .Where(w => w.Length >= 4)
            .ToHashSet(StringComparer.Ordinal);
        return words;
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/').Replace('/', '\\');
        return trimmed;
    }
}
