using System.IO;
using System.Text.Json;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

public class SessionWatcherService : IDisposable
{
    private static string ClaudeProjectsDir => ClaudeConfigService.ClaudeProjectsRoot;

    private static readonly TimeSpan ActiveThreshold = TimeSpan.FromMinutes(10);

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

            var timestamp = root.TryGetProperty("timestamp", out var tsProp) && tsProp.GetString() is string tsStr
                ? DateTimeOffset.Parse(tsStr)
                : DateTimeOffset.UtcNow;

            session.LastSeen = timestamp;

            if (root.TryGetProperty("slug", out var slugProp))
            {
                session.Slug = slugProp.GetString();
            }

            if (type == "custom-title")
            {
                if (root.TryGetProperty("customTitle", out var titleProp))
                {
                    session.CustomName = titleProp.GetString();
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
                if (session.FirstMessage == null)
                {
                    session.FirstMessage = ExtractMessageText(root);
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

                        // Only count as a "turn" if this is a user-facing response
                        // (has text or thinking content), not a pure tool_use continuation.
                        // Without this, each Bash/Read/Edit call inflates the turn count ~5x.
                        if (IsUserFacingResponse(msgProp))
                        {
                            session.TurnCount++;
                            session.TurnTimestamps.Add(timestamp);
                        }

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

    private static string? ExtractMessageText(JsonElement root)
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
            text = content.GetString();
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var tp) && tp.GetString() == "text" &&
                    item.TryGetProperty("text", out var txt))
                {
                    text = txt.GetString();
                    break;
                }
            }
        }

        if (text != null && text.Length > 100)
        {
            text = text[..100];
        }

        return text;
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
    private static bool IsUserFacingResponse(JsonElement msgElement)
    {
        if (!msgElement.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return true; // No content array = count it (safe default)
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var typeProp))
            {
                var t = typeProp.GetString();
                if (t == "text" || t == "thinking")
                {
                    return true;
                }
            }
        }

        return false; // Only tool_use items = not a user-facing turn
    }

    private static string ExtractProjectName(string filePath)
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

    public AggregatedTokenData GetAggregated()
    {
        lock (_lock)
        {
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
        var updated = false;

        // Read live names from ~/.claude/sessions/*.json (ephemeral PID files)
        var sessionsDir = Path.Combine(ClaudeConfigService.ClaudeHomeDir, "sessions");

        if (Directory.Exists(sessionsDir))
        {
            foreach (var file in Directory.GetFiles(sessionsDir, "*.json"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var doc = JsonDocument.Parse(stream);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("sessionId", out var sidProp) &&
                        root.TryGetProperty("name", out var nameProp))
                    {
                        var sid = sidProp.GetString();
                        var name = nameProp.GetString();
                        if (sid != null && !string.IsNullOrEmpty(name))
                        {
                            if (!nameCache.TryGetValue(sid, out var cached) || cached != name)
                            {
                                nameCache[sid] = name;
                                updated = true;
                            }
                        }
                    }
                }
                catch
                {
                    // Skip unreadable files
                }
            }
        }

        // Apply names to sessions
        foreach (var (sid, name) in nameCache)
        {
            if (sessions.TryGetValue(sid, out var session))
            {
                session.CustomName = name;
            }
        }

        if (updated)
        {
            SaveNameCache(nameCache);
        }
    }

    private static Dictionary<string, string> LoadNameCache()
    {
        if (!File.Exists(NameCachePath))
        {
            return [];
        }

        try
        {
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
                    if (session.FirstMessage == null)
                    {
                        session.FirstMessage = ExtractMessageText(root);
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
                        if (IsUserFacingResponse(msgProp))
                        {
                            session.TurnCount++;
                            session.TurnTimestamps.Add(timestamp);
                        }
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
