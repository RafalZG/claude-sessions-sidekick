using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

public class SessionWatcherService : IDisposable
{
    private static string ClaudeProjectsDir => ClaudeConfigService.ClaudeProjectsRoot;

    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromMinutes(10);

    /// <summary>A prompt shorter than this is an acknowledgement ("ok",
    /// "tak", "fix it") — useless as a session label.</summary>
    internal const int MinMeaningfulPromptLength = 15;

    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, SessionTokenData> _sessions = [];
    private readonly Dictionary<string, long> _filePositions = [];
    private readonly object _lock = new();

    public event Action? DataChanged;

    public void Start()
    {
        if (!Directory.Exists(ClaudeProjectsDir))
        {
            return;
        }

        // Initial scan of recently modified files
        ScanRecentFiles();

        _watcher = new FileSystemWatcher(ClaudeProjectsDir, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.Contains("subagents"))
        {
            return;
        }

        try
        {
            ReadNewLines(e.FullPath);
            DataChanged?.Invoke();
        }
        catch
        {
            // File may be locked by Claude Code, skip this update
        }
    }

    private void ScanRecentFiles()
    {
        if (!Directory.Exists(ClaudeProjectsDir))
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-6);

        foreach (var file in Directory.GetFiles(ClaudeProjectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            if (file.Contains("subagents"))
            {
                continue;
            }

            try
            {
                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (lastWrite > cutoff)
                {
                    ReadNewLines(file);
                }
            }
            catch
            {
                // Skip files we can't read
            }
        }
    }

    private void ReadNewLines(string filePath)
    {
        lock (_lock)
        {
            _filePositions.TryGetValue(filePath, out var lastPos);

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= lastPos)
            {
                return;
            }

            stream.Seek(lastPos, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ProcessLine(line, filePath);
            }

            _filePositions[filePath] = stream.Position;
        }
    }

    internal void ProcessLine(string line, string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            var type = typeProp.GetString();
            var sessionId = root.TryGetProperty("sessionId", out var sidProp)
                ? sidProp.GetString() ?? Path.GetFileNameWithoutExtension(filePath)
                : Path.GetFileNameWithoutExtension(filePath);

            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new SessionTokenData
                {
                    SessionId = sessionId,
                    FilePath = filePath,
                    ProjectName = ExtractProjectName(filePath),
                    FirstSeen = DateTimeOffset.UtcNow,
                    // Model shorthand detection happens when Cwd is set (needs project root)
                    ConfiguredModelIsShorthand = ClaudeConfigService.IsModelShorthand(
                        ClaudeConfigService.GetConfiguredModel())
                };
                _sessions[sessionId] = session;
            }

            // Only update LastSeen when the JSONL line carries its own
            // timestamp. The previous fallback to DateTimeOffset.UtcNow
            // pumped LastSeen forward whenever Claude Code emitted a
            // timestamp-less line (custom-title, summary, occasional
            // metadata entries), making closed sessions appear "active"
            // in the widget for the full 10-min ActiveThreshold after
            // the user had stopped using them.
            DateTimeOffset? timestamp = null;
            if (root.TryGetProperty("timestamp", out var tsProp)
                && tsProp.GetString() is string tsStr
                && DateTimeOffset.TryParse(tsStr, out var parsed))
            {
                timestamp = parsed;
            }

            if (timestamp.HasValue)
            {
                session.LastSeen = timestamp.Value;
            }

            if (root.TryGetProperty("slug", out var slugProp))
            {
                session.Slug = slugProp.GetString();
            }

            if (type == "custom-title")
            {
                if (root.TryGetProperty("customTitle", out var titleProp))
                {
                    session.CustomName = titleProp.GetString();
                    session.CustomNameFromRename = true;
                }
            }
            else if (type == "user")
            {
                if (root.TryGetProperty("gitBranch", out var branchProp))
                {
                    session.GitBranch = branchProp.GetString();
                }
                if (session.Cwd == null && root.TryGetProperty("cwd", out var cwdProp))
                {
                    // Keep only the first cwd (project root) — later entries may be
                    // subdirectories that Claude Code cd'd into during the session.
                    session.Cwd = cwdProp.GetString();
                    // Re-check model with project root — project-level settings
                    // may override user-level (e.g. "model": "opus" in project settings)
                    var configuredModel = ClaudeConfigService.GetConfiguredModel(session.Cwd);
                    session.ConfiguredModelIsShorthand = ClaudeConfigService.IsModelShorthand(configuredModel);
                }
                var messageText = ExtractMessageText(root);
                if (session.FirstMessage == null)
                {
                    session.FirstMessage = messageText;
                }
                // Count REAL user turns (typed prompts), not tool_result returns.
                // This is the only place TurnCount/TurnTimestamps grow now —
                // assistant-side counting was inflating ~5-15× because of tool rounds.
                if (IsUserTypedMessage(root))
                {
                    UpdateLastMeaningfulPrompt(session, messageText);
                    session.TurnCount++;
                    if (timestamp.HasValue)
                    {
                        session.TurnTimestamps.Add(timestamp.Value);
                    }
                }
            }
            else if (type == "assistant")
            {
                if (root.TryGetProperty("message", out var msgProp))
                {
                    if (msgProp.TryGetProperty("model", out var modelProp))
                    {
                        session.Model = modelProp.GetString();
                    }

                    if (msgProp.TryGetProperty("usage", out var usageProp))
                    {
                        var turnInput = GetLong(usageProp, "input_tokens");
                        var turnOutput = GetLong(usageProp, "output_tokens");
                        var turnCacheRead = GetLong(usageProp, "cache_read_input_tokens");
                        var turnCacheCreation = GetLong(usageProp, "cache_creation_input_tokens");

                        session.InputTokens += turnInput;
                        session.OutputTokens += turnOutput;
                        session.CacheReadTokens += turnCacheRead;
                        session.CacheCreationTokens += turnCacheCreation;

                        // Turn counting moved to the type=="user" branch — see
                        // IsUserTypedMessage docs. Counting per assistant message
                        // (even with the text/thinking heuristic) inflates by the
                        // number of tool rounds within a turn (typically 5-15x).

                        session.PrevTurnContextSize = session.LastTurnContextSize;
                        var turnContext = turnInput + turnCacheRead + turnCacheCreation;
                        session.LastTurnContextSize = turnContext;
                        session.LastTurnInput = turnInput;
                        session.LastTurnCacheRead = turnCacheRead;
                        session.LastTurnCacheCreation = turnCacheCreation;

                        if (turnContext > session.MaxObservedContext)
                        {
                            session.MaxObservedContext = turnContext;
                        }

                        // Diagnostic: a real Anthropic API prompt cannot exceed the
                        // model's context window — anything noticeably above 100%
                        // means our accounting or the upstream usage report is off.
                        // Latch per session so a long anomalous session doesn't
                        // spam app.log with one warning per turn.
                        var contextWindow = session.ContextWindowSize;
                        if (contextWindow > 0
                            && turnContext > contextWindow * 1.05
                            && !session.DiagnosticHighContextLogged)
                        {
                            AppLogger.Warn(
                                $"High-context anomaly: sessionId={sessionId} " +
                                $"model={session.Model ?? "?"} " +
                                $"shorthand={session.ConfiguredModelIsShorthand} " +
                                $"context={turnContext:N0} window={contextWindow:N0} " +
                                $"(input={turnInput:N0} cacheRead={turnCacheRead:N0} " +
                                $"cacheCreation={turnCacheCreation:N0}) " +
                                $"turn={session.TurnCount} " +
                                $"file={Path.GetFileName(filePath)}");
                            session.DiagnosticHighContextLogged = true;
                        }
                    }
                }
            }
            else if (type == "compact")
            {
                // compactMetadata.preTokens = context size just before auto-compact
                if (root.TryGetProperty("compactMetadata", out var cm) &&
                    cm.ValueKind == JsonValueKind.Object)
                {
                    var trigger = cm.TryGetProperty("trigger", out var trigProp) ? trigProp.GetString() : null;
                    if (trigger == "auto" && cm.TryGetProperty("preTokens", out var preProp))
                    {
                        session.AutoCompactThreshold = preProp.GetInt64();
                    }
                }
            }
        }
        catch
        {
            // Malformed line, skip
        }
    }

    internal static string? ExtractMessageText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg))
        {
            return null;
        }

        if (!msg.TryGetProperty("content", out var content))
        {
            return null;
        }

        string? text = null;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = UnwrapPastedContent(content.GetString());
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var tp) && tp.GetString() == "text" &&
                    item.TryGetProperty("text", out var txt))
                {
                    var blockText = UnwrapPastedContent(txt.GetString());
                    if (string.IsNullOrWhiteSpace(blockText))
                    {
                        continue;
                    }
                    // A record can carry a harness block AND the real prompt —
                    // skip the former so the latter still names the session.
                    if (IsSystemGeneratedMessage(blockText))
                    {
                        continue;
                    }
                    text = blockText;
                    break;
                }
            }
        }

        if (text != null)
        {
            // Labels are single-line UI strings and get copied into the state
            // dump in app.log — a newline or ANSI escape inside a paste or a
            // multi-line prompt must not forge lines there. Format characters
            // (category Cf: BiDi overrides, zero-width) go too - they can
            // visually reverse or disguise a label in the restore list.
            var normalizedChars = text
                .Select(c => char.IsControl(c)
                    || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format
                    || c == '\u2028'
                    || c == '\u2029'
                    ? ' '
                    : c)
                .ToArray();
            text = new string(normalizedChars).Trim();
            if (text.Length == 0)
            {
                return null;
            }
        }

        if (text != null && IsSystemGeneratedMessage(text))
        {
            // Not something the user typed — returning null keeps FirstMessage
            // empty so a later REAL prompt can fill it.
            return null;
        }

        if (text != null && text.Length > 100)
        {
            // Don't cut a surrogate pair (emoji) in half.
            var cut = char.IsHighSurrogate(text[99]) ? 99 : 100;
            text = text[..cut];
        }

        return text;
    }

    // NonBacktracking = guaranteed linear time even on a hostile paste made
    // of thousands of unterminated tags (a backtracking engine goes
    // quadratic there, and ProcessLine holds the watcher lock).
    private static readonly Regex PastedContentTagRegex = new(
        @"</?pasted_content(\s[^>]*)?>", RegexOptions.NonBacktracking | RegexOptions.IgnoreCase);

    private static readonly Regex WhitespaceRunRegex = new(
        @"\s+", RegexOptions.NonBacktracking);

    /// <summary>A paste lands in the transcript wrapped in
    /// &lt;pasted_content&gt; tags. The PASTED TEXT itself (a URL, an error,
    /// a snippet) is often the best session label there is — in a session
    /// where the user never typed a prompt it's the only one — so drop the
    /// wrapper and keep what's inside instead of discarding the record.
    /// Pastes are often multi-line, and labels are single-line UI strings,
    /// so whitespace runs collapse to one space.</summary>
    internal static string? UnwrapPastedContent(string? text)
    {
        if (text == null || !text.Contains("pasted_content", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        var withoutTags = PastedContentTagRegex.Replace(text, " ");
        if (withoutTags == text)
        {
            // The word appeared but no actual tag — leave the prompt alone.
            return text;
        }

        var stripped = WhitespaceRunRegex.Replace(withoutTags, " ").Trim();
        var result = stripped.Length == 0 ? null : stripped;
        return result;
    }

    private static void UpdateLastMeaningfulPrompt(SessionTokenData session, string? messageText)
    {
        if (messageText != null && messageText.Trim().Length >= MinMeaningfulPromptLength)
        {
            session.LastMeaningfulPrompt = messageText;
        }
    }

    // Records the harness writes into the transcript around slash commands,
    // interrupts and background tasks. They are useless as a session name
    // ("<local-command-caveat>Caveat: The messages below...") — skip them when
    // picking the first message (MonitorMike's naming report, 2026-10-14).
    private static readonly string[] SystemMessagePrefixes =
    {
        "<local-command-caveat",
        "<command-name",
        "<command-message",
        "<local-command-stdout",
        "<system-reminder",
        "<task-notification",
        "Caveat: The messages below",
        "[Request interrupted",
        // Injected after /compact or auto-compact — not something the user typed.
        "This session is being continued",
        // Agent-to-agent traffic — also not typed by the user.
        "<agent-message",
        "Another Claude session sent a message",
        // Well-formed paste wrappers are unwrapped BEFORE this check (the
        // pasted text makes a good label); this catches only a broken tag.
        "<pasted_content",
    };

    internal static bool IsSystemGeneratedMessage(string text)
    {
        var trimmed = text.TrimStart();
        foreach (var prefix in SystemMessagePrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long GetLong(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var prop) ? prop.GetInt64() : 0;
    }

    /// <summary>
    /// Returns true if the assistant message contains user-facing content (text or thinking),
    /// not just tool_use calls. Used to count meaningful "turns" — a single user question
    /// that triggers 5 tool calls should count as 1 turn, not 6.
    /// </summary>
    /// <summary>
    /// True when a <c>type:"user"</c> JSONL entry represents a real user-typed
    /// message rather than a tool result returning to the model. The distinction
    /// matters for turn counting — without it, a single user prompt that
    /// triggers 10 tool rounds inflates "Turns" by 10× because Claude Code
    /// records each tool_result + each assistant reply as separate JSONL
    /// entries with usage data, and each of those previously counted as a
    /// "turn" via the assistant-side heuristic.
    /// </summary>
    internal static bool IsUserTypedMessage(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("message", out var msg))
        {
            return false;
        }
        if (!msg.TryGetProperty("content", out var content))
        {
            return false;
        }
        // Plain string content = user typed (the common case for short prompts).
        if (content.ValueKind == JsonValueKind.String)
        {
            return true;
        }
        // Array content — exclude entries containing tool_result items;
        // a tool_result is the SDK returning data to the model, not a turn.
        if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() == "tool_result")
                {
                    return false;
                }
            }
            return true; // text/image items only = user typed (e.g. pasted screenshot)
        }
        return false;
    }

    // Exposed `internal` so the test suite can pin down the
    // project-key collision shape Ewa hit ("active session header shows
    // different session" — two distinct real folders that encode to keys
    // whose last two dash-segments are identical end up sharing a
    // ProjectName, so the header can land on either one).
    internal static string ExtractProjectName(string filePath)
    {
        // Path: .../projects/D--Projects-MyApp-Main/session.jsonl
        var dir = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";

        // Take last meaningful segment: "ExergyERP-Dev" from "D--Projects-MyApp-Main"
        var parts = dir.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            // Find last group that looks like a project name (skip drive letter segments)
            var meaningful = new List<string>();
            foreach (var part in parts)
            {
                if (part.Length <= 1 && char.IsLetter(part[0]))
                {
                    meaningful.Clear(); // Reset after drive letter
                    continue;
                }
                meaningful.Add(part);
            }
            return string.Join("-", meaningful.TakeLast(2));
        }

        return dir;
    }

    // Assigns each session a display ProjectName derived from its recorded Cwd
    // (the real filesystem path) instead of the lossy encoded folder key. In the
    // key, the drive colon, path separators, AND literal '-'/'_' in folder names
    // all collapse to '-', so three distinct real folders — ExergyERP_Dev,
    // ExergyERP-Dev, ExergyERP\Dev — share one key and one ProjectName. That is
    // Ewa's "active session header shows a different session": two sessions become
    // visually indistinguishable. Cwd is unambiguous, so we key off it instead.
    //
    // The label is the shortest leaf-anchored tail of the Cwd path that no OTHER
    // distinct Cwd in the set shares. Non-colliding projects stay short (just the
    // leaf folder); only genuinely-colliding ones grow leftward (parent\leaf) and
    // only as far as needed to become unique. Sessions with no Cwd keep the
    // key-derived ExtractProjectName fallback.
    internal static void AssignProjectNames(IEnumerable<SessionTokenData> sessions)
    {
        var list = sessions as ICollection<SessionTokenData> ?? sessions.ToList();

        // Split each distinct Cwd into path segments once.
        var segmentsByPath = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in list)
        {
            if (string.IsNullOrWhiteSpace(s.Cwd))
            {
                continue;
            }
            var norm = s.Cwd!.TrimEnd('\\', '/');
            if (!segmentsByPath.ContainsKey(norm))
            {
                var segs = norm.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length > 0)
                {
                    segmentsByPath[norm] = segs;
                }
            }
        }

        // For each distinct path, pick the shortest leaf-anchored tail unique
        // among all other distinct paths.
        var labelByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, segs) in segmentsByPath)
        {
            var label = string.Join("\\", segs);
            for (var k = 1; k <= segs.Length; k++)
            {
                var tail = string.Join("\\", segs[^k..]);
                var clash = false;
                foreach (var (otherPath, otherSegs) in segmentsByPath)
                {
                    if (otherPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (otherSegs.Length >= k &&
                        string.Join("\\", otherSegs[^k..]).Equals(tail, StringComparison.OrdinalIgnoreCase))
                    {
                        clash = true;
                        break;
                    }
                }
                label = tail;
                if (!clash)
                {
                    break;
                }
            }
            labelByPath[path] = label;
        }

        // Apply. Sessions sharing a Cwd get the same label (same project).
        foreach (var s in list)
        {
            if (string.IsNullOrWhiteSpace(s.Cwd))
            {
                continue;
            }
            var norm = s.Cwd!.TrimEnd('\\', '/');
            if (labelByPath.TryGetValue(norm, out var label))
            {
                s.ProjectName = label;
            }
        }
    }

    public AggregatedTokenData GetAggregated()
    {
        lock (_lock)
        {
            // Disambiguate over the full universe so the widget's project label
            // for a session matches the Session Browser's (Ewa collision fix).
            AssignProjectNames(_sessions.Values);

            var activeSessions = _sessions.Values
                .Where(s => s.IsActive(ActiveThreshold))
                .OrderByDescending(s => s.LastSeen)
                .ToList();

            // Cap by the number of running Claude Code CLI processes - the JSONL
            // last-write timestamp keeps a session "active" for ~10 minutes after the
            // user closes Claude, which over-reports the count and pollutes the widget.
            // If we can count running processes, take only the top-N most recent.
            // -1 = scan failed, 0 = no processes found (Desktop App may not be detected).
            // Only cap when we positively found SOME processes — capping to 0 would hide
            // all sessions even when the user is actively using Claude Desktop App.
            var runningCount = ClaudeProcessService.GetRunningClaudeCodeCount();
            if (runningCount > 0 && runningCount < activeSessions.Count)
            {
                activeSessions = activeSessions.Take(runningCount).ToList();
            }

            return new AggregatedTokenData
            {
                ActiveSessionCount = activeSessions.Count,
                TotalInputTokens = activeSessions.Sum(s => s.InputTokens),
                TotalOutputTokens = activeSessions.Sum(s => s.OutputTokens),
                TotalCacheTokens = activeSessions.Sum(s => s.CacheReadTokens + s.CacheCreationTokens),
                Sessions = activeSessions
            };
        }
    }

    /// <summary>
    /// Returns all sessions from the last N hours, for expanded view.
    /// </summary>
    public List<SessionTokenData> GetRecentSessions(int hours = 6)
    {
        lock (_lock)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-hours);
            return _sessions.Values
                .Where(s => s.LastSeen > cutoff)
                .OrderByDescending(s => s.LastSeen)
                .ToList();
        }
    }

    /// <summary>
    /// Scans ALL session files (for session browser). Returns a new list, doesn't affect watcher state.
    /// </summary>
    public static List<SessionTokenData> ScanAllSessions()
    {
        var sessions = new Dictionary<string, SessionTokenData>();

        if (!Directory.Exists(ClaudeProjectsDir))
        {
            return [];
        }

        foreach (var file in Directory.GetFiles(ClaudeProjectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            if (file.Contains("subagents"))
            {
                continue;
            }

            try
            {
                ScanFileForBrowser(file, sessions);
            }
            catch
            {
                // Skip unreadable files
            }
        }

        // Load custom session names from ~/.claude/sessions/*.json
        LoadSessionNames(sessions);

        // Disambiguate project names from real Cwd paths (Ewa collision fix).
        AssignProjectNames(sessions.Values);

        return sessions.Values
            .Where(s => s.TurnCount > 0)
            .OrderByDescending(s => s.LastSeen)
            .ToList();
    }

    private static readonly string NameCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeSessionsSidekick", "session-names.json");

    private static void LoadSessionNames(Dictionary<string, SessionTokenData> sessions)
    {
        var nameCache = LoadNameCache();

        // One guarded reader for ~/.claude/sessions/*.json: pid in the file
        // must match the file name, GUID session id, sanitized name, size cap.
        var pidInfos = ClaudeSessionPidFileService.ReadAllSessionFiles();

        var updated = MergeNameCache(nameCache, pidInfos, sessions);
        if (updated)
        {
            SaveNameCache(nameCache);
        }
    }

    /// <summary>Feeds user-typed names from the pid files into the cache,
    /// drops leftovers an older Sidekick saved that were never real names,
    /// and puts the surviving names onto the sessions. Returns true when the
    /// cache changed and needs saving. Expects <paramref name="sessions"/> to
    /// hold EVERY session found on disk — entries for unknown sessions are
    /// treated as orphans and dropped.</summary>
    internal static bool MergeNameCache(
        Dictionary<string, string> nameCache
        , IReadOnlyList<ClaudeSessionPidInfo> pidInfos
        , Dictionary<string, SessionTokenData> sessions)
    {
        var updated = false;

        // Session ids the pid files vouch for right now. "user" means the
        // user typed the name (/rename); those names may look like anything —
        // even like Claude's junk labels — and must never be purged.
        var userNamedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownPidFileSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in pidInfos)
        {
            knownPidFileSids.Add(info.SessionId);

            if (info.HasUserName)
            {
                userNamedSids.Add(info.SessionId);
                if (!nameCache.TryGetValue(info.SessionId, out var cached) || cached != info.Name)
                {
                    nameCache[info.SessionId] = info.Name!;
                    updated = true;
                }
            }
            else if (info.NameSource == "derived"
                && info.Name != null
                && nameCache.TryGetValue(info.SessionId, out var cached)
                && cached == info.Name)
            {
                // Claude's own throwaway label (like "dev-63") that an older
                // Sidekick already cached — forget it, so the real name
                // (slug / last prompt) can show again. Only an explicit
                // "derived" marker justifies deleting; a missing nameSource
                // (older CLI) must not erase anything.
                nameCache.Remove(info.SessionId);
                updated = true;
            }
        }

        // Apply names to sessions. A Claude Code /rename (custom-title in the
        // JSONL) is authoritative and must NOT be clobbered by this cache.
        foreach (var (sid, name) in nameCache.ToList())
        {
            if (!sessions.TryGetValue(sid, out var session))
            {
                // No session file anywhere on disk and no pid file either —
                // the entry can never be shown again, drop it. (A pid file
                // can exist moments before the session file appears, so
                // those are kept.)
                if (!knownPidFileSids.Contains(sid))
                {
                    nameCache.Remove(sid);
                    updated = true;
                }
                continue;
            }

            if (!userNamedSids.Contains(sid) && IsJunkCachedName(name, sid, session.Cwd))
            {
                nameCache.Remove(sid);
                updated = true;
                continue;
            }

            if (!session.CustomNameFromRename)
            {
                session.CustomName = name;
            }
        }

        return updated;
    }

    /// <summary>A cached name an older Sidekick saved that was never really a
    /// name: Claude's folder-based auto label ("exergyerp-dev-6c"), or the
    /// session's own id prefix echoed back ("423cea84").</summary>
    internal static bool IsJunkCachedName(string name, string sessionId, string? cwd)
    {
        if (ClaudeSessionPidFileService.IsDerivedAutoName(name, cwd))
        {
            return true;
        }

        var isIdPrefix = sessionId.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && name.Length >= 8;
        return isIdPrefix;
    }

    private static Dictionary<string, string> LoadNameCache()
    {
        try
        {
            var fileInfo = new FileInfo(NameCachePath);
            // A healthy cache is a few KB; anything above 5MB is corruption
            // or tampering — start fresh rather than slurp it.
            if (!fileInfo.Exists || fileInfo.Length > 5 * 1024 * 1024)
            {
                return [];
            }

            var json = File.ReadAllText(NameCachePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void SaveNameCache(Dictionary<string, string> cache)
    {
        try
        {
            var dir = Path.GetDirectoryName(NameCachePath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(NameCachePath, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // Non-critical
        }
    }

    private static void ScanFileForBrowser(string filePath, Dictionary<string, SessionTokenData> sessions)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var sessionId = Path.GetFileNameWithoutExtension(filePath);
        SessionTokenData? session = null;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeProp))
                {
                    continue;
                }

                var type = typeProp.GetString();

                var sid = root.TryGetProperty("sessionId", out var sidProp)
                    ? sidProp.GetString() ?? sessionId
                    : sessionId;

                if (!sessions.TryGetValue(sid, out session))
                {
                    session = new SessionTokenData
                    {
                        SessionId = sid,
                        FilePath = filePath,
                        ProjectName = ExtractProjectName(filePath),
                        ConfiguredModelIsShorthand = ClaudeConfigService.IsModelShorthand(
                            ClaudeConfigService.GetConfiguredModel())
                    };
                    sessions[sid] = session;
                }

                var timestamp = root.TryGetProperty("timestamp", out var tsProp) && tsProp.GetString() is string tsStr
                    ? DateTimeOffset.Parse(tsStr)
                    : DateTimeOffset.MinValue;

                if (session.FirstSeen == default || (timestamp != DateTimeOffset.MinValue && timestamp < session.FirstSeen))
                {
                    session.FirstSeen = timestamp;
                }

                if (timestamp > session.LastSeen)
                {
                    session.LastSeen = timestamp;
                }

                if (root.TryGetProperty("slug", out var slugProp2))
                {
                    session.Slug = slugProp2.GetString();
                }

                if (type == "custom-title")
                {
                    if (root.TryGetProperty("customTitle", out var titleProp))
                    {
                        session.CustomName = titleProp.GetString();
                        session.CustomNameFromRename = true;
                    }
                }
                else if (type == "user")
                {
                    if (root.TryGetProperty("gitBranch", out var branchProp))
                    {
                        session.GitBranch = branchProp.GetString();
                    }
                    if (session.Cwd == null && root.TryGetProperty("cwd", out var cwdProp))
                    {
                        session.Cwd = cwdProp.GetString();
                        // Re-check with project root for project-level model settings
                        var cfgModel = ClaudeConfigService.GetConfiguredModel(session.Cwd);
                        session.ConfiguredModelIsShorthand = ClaudeConfigService.IsModelShorthand(cfgModel);
                    }
                    var messageText = ExtractMessageText(root);
                    if (session.FirstMessage == null)
                    {
                        session.FirstMessage = messageText;
                    }
                    // Real-user-typed messages only (see IsUserTypedMessage).
                    if (IsUserTypedMessage(root))
                    {
                        UpdateLastMeaningfulPrompt(session, messageText);
                        session.TurnCount++;
                        session.TurnTimestamps.Add(timestamp);
                    }
                }
                else if (type == "assistant" && root.TryGetProperty("message", out var msgProp))
                {
                    if (msgProp.TryGetProperty("model", out var modelProp))
                    {
                        session.Model = modelProp.GetString();
                    }

                    if (msgProp.TryGetProperty("usage", out var usageProp))
                    {
                        var ti = GetLong(usageProp, "input_tokens");
                        var tcr = GetLong(usageProp, "cache_read_input_tokens");
                        var tcc = GetLong(usageProp, "cache_creation_input_tokens");

                        session.InputTokens += ti;
                        session.OutputTokens += GetLong(usageProp, "output_tokens");
                        session.CacheReadTokens += tcr;
                        session.CacheCreationTokens += tcc;
                        // Turn count handled in the type=="user" branch above.
                        var ctx = ti + tcr + tcc;
                        session.LastTurnContextSize = ctx;
                        if (ctx > session.MaxObservedContext)
                        {
                            session.MaxObservedContext = ctx;
                        }
                    }
                }
                else if (type == "compact")
                {
                    if (root.TryGetProperty("compactMetadata", out var cm) &&
                        cm.ValueKind == JsonValueKind.Object)
                    {
                        var trigger = cm.TryGetProperty("trigger", out var trigProp) ? trigProp.GetString() : null;
                        if (trigger == "auto" && cm.TryGetProperty("preTokens", out var preProp))
                        {
                            session.AutoCompactThreshold = preProp.GetInt64();
                        }
                    }
                }
            }
            catch
            {
                // Skip malformed lines
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
