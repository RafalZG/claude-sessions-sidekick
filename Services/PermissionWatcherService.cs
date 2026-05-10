using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Watches Claude Code settings files for newly-added permission rules.
/// When a user clicks "Don't ask again" in Claude Code, the rule is appended
/// to a settings file (global or project-level). We detect it, check if it's
/// overly-specific, and fire an event so the UI can show a generalization suggestion.
///
/// Watches:
/// - ~/.claude/settings.json (global)
/// - {projectRoot}/.claude/settings.local.json (per Quick Launch project)
/// </summary>
public class PermissionWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
    private HashSet<string> _knownRules = [];
    private IReadOnlyList<string> _watchedFiles = Array.Empty<string>();
    private long _lastProcessedTicks;

    /// <summary>Fired when a new rule is detected that could be generalized.</summary>
    public event Action<PermissionSuggestion>? SuggestionReady;

    /// <summary>
    /// Starts watching the global settings and project-level settings for each
    /// Quick Launch project folder.
    /// </summary>
    public void Start(List<QuickLaunchEntry>? quickLaunchEntries = null)
    {
        // Build the list of files to watch, then freeze it (write-once, read-many)
        var files = new List<string> { PermissionService.GlobalSettingsPath };

        // Project-level settings.local.json for each Quick Launch project
        if (quickLaunchEntries != null)
        {
            foreach (var entry in quickLaunchEntries)
            {
                if (string.IsNullOrEmpty(entry.FolderPath) || !Directory.Exists(entry.FolderPath))
                {
                    continue;
                }

                var localPath = PermissionService.GetProjectLocalSettingsPath(entry.FolderPath);
                if (!files.Contains(localPath, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(localPath);
                }

                var sharedPath = PermissionService.GetProjectSettingsPath(entry.FolderPath);
                if (!files.Contains(sharedPath, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(sharedPath);
                }
            }
        }

        _watchedFiles = files.AsReadOnly();

        // Snapshot current rules across all files
        _knownRules = LoadAllRuleStrings();

        // Create watchers for each unique directory
        var watchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in _watchedFiles)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }

            // Create the .claude directory if it doesn't exist yet — it will when
            // Claude Code first writes a project-level rule
            if (!Directory.Exists(dir))
            {
                continue;
            }

            if (!watchedDirs.Add(dir))
            {
                continue; // Already watching this directory
            }

            try
            {
                var watcher = new FileSystemWatcher(dir, "settings*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnSettingsChanged;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"PermissionWatcher: failed to watch {dir}: {ex.Message}");
            }
        }

        if (_watchers.Count > 0)
        {
            AppLogger.Info($"PermissionWatcher: watching {_watchers.Count} directory(ies), {_watchedFiles.Count} file(s), {_knownRules.Count} existing rules");
        }
    }

    private void OnSettingsChanged(object sender, FileSystemEventArgs e)
    {
        // Only react to files we care about
        if (!_watchedFiles.Any(f => string.Equals(f, e.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Debounce — FileSystemWatcher often fires multiple events for one save.
        // Only update the timestamp when we actually process, not when suppressed,
        // so the debounce window doesn't drift on rapid-fire events.
        var nowTicks = DateTime.UtcNow.Ticks;
        var prev = Interlocked.Read(ref _lastProcessedTicks);
        if (nowTicks - prev < TimeSpan.FromSeconds(2).Ticks)
        {
            return;
        }
        Interlocked.Exchange(ref _lastProcessedTicks, nowTicks);

        try
        {
            // Retry with backoff — file may still be locked by the writer
            HashSet<string>? currentRules = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Thread.Sleep(200 * (attempt + 1));
                    currentRules = LoadAllRuleStrings();
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    // File still locked, retry
                }
            }

            if (currentRules == null)
            {
                return;
            }

            List<(string ruleStr, List<string> suggestions, string sourceFile)> pending;
            lock (_lock)
            {
                var newRules = currentRules.Except(_knownRules).ToList();
                _knownRules = currentRules;

                if (newRules.Count == 0)
                {
                    return;
                }

                pending = [];
                foreach (var ruleStr in newRules)
                {
                    var rule = PermissionRule.Parse(ruleStr, PermissionScope.Allow);
                    if (rule == null)
                    {
                        continue;
                    }

                    var suggestions = GetSuggestions(rule);
                    if (suggestions.Count > 0)
                    {
                        // Find which file contains this rule
                        var sourceFile = FindFileContainingRule(ruleStr);
                        pending.Add((ruleStr, suggestions, sourceFile));
                    }
                }
            }

            // Fire events outside the lock
            foreach (var (ruleStr, suggestions, sourceFile) in pending)
            {
                AppLogger.Info($"PermissionWatcher: new rule '{ruleStr}' in {Path.GetFileName(sourceFile)} — {suggestions.Count} suggestion(s)");
                SuggestionReady?.Invoke(new PermissionSuggestion
                {
                    OriginalRule = ruleStr,
                    Suggestions = suggestions,
                    SourceFile = sourceFile
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"PermissionWatcher: error processing change: {ex.Message}");
        }
    }

    /// <summary>
    /// Analyzes a rule and returns generalization suggestions.
    /// Handles both simple commands and compound commands (cd && grep).
    /// </summary>
    internal static List<string> GetSuggestions(PermissionRule rule)
    {
        var suggestions = new List<string>();

        if (rule.Tool != "Bash" || string.IsNullOrEmpty(rule.Pattern))
        {
            return suggestions;
        }

        // Check for compound commands: cd /path && grep something
        // Split on && and || and ; to analyze each part
        var parts = SplitCompoundCommand(rule.Pattern);
        if (parts.Count > 1)
        {
            foreach (var part in parts)
            {
                var partRule = new PermissionRule { Tool = "Bash", Pattern = part.Trim(), Scope = rule.Scope };
                var generalized = partRule.SuggestGeneralizedPattern();
                if (generalized != null)
                {
                    suggestions.Add($"Bash({generalized})");
                }
            }
            return suggestions;
        }

        // Simple command — use existing generalization
        var simple = rule.SuggestGeneralizedPattern();
        if (simple != null)
        {
            suggestions.Add($"Bash({simple})");
        }

        return suggestions;
    }

    /// <summary>
    /// Splits a compound shell command on &&, ||, and ; operators.
    /// Returns the individual command parts.
    /// </summary>
    internal static List<string> SplitCompoundCommand(string command)
    {
        var parts = new List<string>();
        var current = 0;

        while (current < command.Length)
        {
            // Find next separator: && or || or ;
            var nextAnd = command.IndexOf("&&", current, StringComparison.Ordinal);
            var nextOr = command.IndexOf("||", current, StringComparison.Ordinal);
            var nextSemi = command.IndexOf(';', current);

            var nextSep = int.MaxValue;
            var sepLen = 1;

            if (nextAnd >= 0 && nextAnd < nextSep) { nextSep = nextAnd; sepLen = 2; }
            if (nextOr >= 0 && nextOr < nextSep) { nextSep = nextOr; sepLen = 2; }
            if (nextSemi >= 0 && nextSemi < nextSep) { nextSep = nextSemi; sepLen = 1; }

            if (nextSep == int.MaxValue)
            {
                // No more separators
                var remaining = command[current..].Trim();
                if (remaining.Length > 0)
                {
                    parts.Add(remaining);
                }
                break;
            }

            var part = command[current..nextSep].Trim();
            if (part.Length > 0)
            {
                parts.Add(part);
            }
            current = nextSep + sepLen;
        }

        return parts;
    }

    /// <summary>
    /// Applies multiple generalized rules in a single file operation.
    /// Writes to the file where the original rule was found.
    /// </summary>
    public bool ApplySuggestions(PermissionSuggestion suggestion, bool removeOriginal)
    {
        try
        {
            lock (_lock)
            {
                // Write to the file where the original rule lives
                var targetFile = !string.IsNullOrEmpty(suggestion.SourceFile)
                    ? suggestion.SourceFile
                    : PermissionService.GlobalSettingsPath;

                var displayName = Path.GetFileName(Path.GetDirectoryName(targetFile)) + "/" + Path.GetFileName(targetFile);
                var pf = PermissionFile.Load(targetFile, displayName);

                foreach (var generalizedRule in suggestion.Suggestions)
                {
                    var existing = pf.Rules.FirstOrDefault(r => r.RuleString == generalizedRule);
                    if (existing == null)
                    {
                        var parsed = PermissionRule.Parse(generalizedRule, PermissionScope.Allow);
                        if (parsed != null)
                        {
                            pf.Rules.Add(parsed);
                        }
                    }
                }

                if (removeOriginal)
                {
                    pf.Rules.RemoveAll(r => r.RuleString == suggestion.OriginalRule);
                }

                pf.Save();

                // Update known rules snapshot so watcher doesn't re-suggest
                _knownRules = LoadAllRuleStrings();
            }

            var suggestionsText = string.Join(", ", suggestion.Suggestions);
            AppLogger.Info($"PermissionWatcher: applied {suggestion.Suggestions.Count} suggestion(s) [{suggestionsText}]" +
                           (removeOriginal ? $" (removed '{suggestion.OriginalRule}')" : ""));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("PermissionWatcher: failed to apply suggestions", ex);
            return false;
        }
    }

    /// <summary>Finds which watched file contains a given rule string.</summary>
    private string FindFileContainingRule(string ruleStr)
    {
        foreach (var filePath in _watchedFiles)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    continue;
                }

                var json = File.ReadAllText(filePath);
                if (json.Contains(ruleStr, StringComparison.Ordinal))
                {
                    return filePath;
                }
            }
            catch
            {
                // Ignore read errors
            }
        }
        return PermissionService.GlobalSettingsPath;
    }

    /// <summary>Loads rules from ALL watched settings files into one set.</summary>
    private HashSet<string> LoadAllRuleStrings()
    {
        var rules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var filePath in _watchedFiles)
        {
            LoadRulesFromFile(filePath, rules);
        }
        return rules;
    }

    private static void LoadRulesFromFile(string filePath, HashSet<string> rules)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var json = File.ReadAllText(filePath);
            var node = JsonNode.Parse(json);
            if (node?["permissions"] is not JsonObject perms)
            {
                return;
            }

            foreach (var key in new[] { "allow", "deny", "ask" })
            {
                if (perms[key] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is JsonValue v && v.TryGetValue<string>(out var s))
                        {
                            rules.Add(s);
                        }
                    }
                }
            }
        }
        catch
        {
            // Silently skip unreadable files
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }
}

public class PermissionSuggestion
{
    public string OriginalRule { get; set; } = "";
    public List<string> Suggestions { get; set; } = [];
    /// <summary>Which settings file the original rule was found in.</summary>
    public string SourceFile { get; set; } = "";
}
