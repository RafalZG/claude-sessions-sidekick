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
        , string? windowTitle)
    {
        if (sessions.Count == 0 || claudeProcesses.Count == 0)
        {
            return null;
        }

        var candidates = new List<SessionTokenData>();
        foreach (var proc in claudeProcesses)
        {
            var matched = MatchProcessToSession(sessions, proc);
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
        // tabs sharing a single process). The window title shows the active
        // tab's title, which usually contains the session's name — use it to
        // pick the tab the user actually sees.
        var byTitle = NarrowByTitle(candidates, windowTitle);
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
            return commandLine == null
                || commandLine.IndexOf("--type=", StringComparison.OrdinalIgnoreCase) < 0;
        }

        return false;
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
    /// Keeps only the candidates whose name (custom name, slug or project name)
    /// appears in the window title. When the title helps no one, all candidates
    /// stay so the caller can still fall back to "newest of the matched".
    /// </summary>
    private static List<SessionTokenData> NarrowByTitle(
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
        if (narrowed.Count == 0)
        {
            return candidates;
        }

        return narrowed;
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

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim().TrimEnd('\\', '/');
        return trimmed;
    }
}
