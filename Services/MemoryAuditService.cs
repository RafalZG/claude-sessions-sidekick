using System.IO;
using System.Text;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Builds a rough "memory footprint" of a Claude Code setup — every instruction /
/// memory file that feeds a session's context — and decides when to nudge the user
/// to review and consolidate it (the classic "keep memory small" hygiene: dedupe,
/// drop stale entries, promote durable facts to rules/skills).
///
/// Sidekick doesn't do the consolidation itself; it hands the user a ready prompt to
/// paste into Claude Code, which has the tools to read and edit the files. This
/// service only measures (so we know WHEN to nudge and WHAT is bloated) and writes
/// the prompt.
///
/// Token counts are approximate (~bytes/4) by design — no Anthropic tokenizer is
/// available on the OAuth path, and the goal is relative comparison, not billing.
/// </summary>
public static class MemoryAuditService
{
    /// <summary>Rough chars/token ratio for English prose + markdown + code.</summary>
    private const double BytesPerToken = 4.0;

    public static int EstimateTokens(long bytes) =>
        bytes <= 0 ? 0 : (int)Math.Ceiling(bytes / BytesPerToken);

    /// <summary>
    /// Enumerates every memory/instruction file feeding Claude Code's context:
    /// the global CLAUDE.md/AGENTS.md, each configured project's CLAUDE.md /
    /// AGENTS.md / CLAUDE.local.md and .claude/rules/*.md, and every auto-memory
    /// file under ~/.claude/projects/&lt;key&gt;/memory/. Missing files are skipped;
    /// duplicate paths are collapsed.
    /// </summary>
    public static MemoryAuditResult BuildAudit(IEnumerable<QuickLaunchEntry>? projects)
    {
        var files = new List<MemoryAuditFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string displayName, MemoryFileCategory category)
        {
            if (string.IsNullOrEmpty(path) || !seen.Add(NormalizePath(path)))
            {
                return;
            }
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return;
                }
                files.Add(new MemoryAuditFile
                {
                    Path = path,
                    DisplayName = displayName,
                    Category = category,
                    Bytes = info.Length,
                    EstimatedTokens = EstimateTokens(info.Length),
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"MemoryAudit: could not stat {path}: {ex.Message}");
            }
        }

        // Global instruction files (~/.claude/CLAUDE.md, ~/.claude/AGENTS.md).
        var globalClaudeMd = ClaudeConfigService.GlobalClaudeMdPath;
        var claudeHome = Path.GetDirectoryName(globalClaudeMd);
        Add(globalClaudeMd, "CLAUDE.md (global)", MemoryFileCategory.GlobalInstructions);
        if (!string.IsNullOrEmpty(claudeHome))
        {
            Add(Path.Combine(claudeHome, "AGENTS.md"), "AGENTS.md (global)", MemoryFileCategory.GlobalInstructions);
        }

        // Per-project instruction files + rules.
        foreach (var entry in projects ?? [])
        {
            var folder = entry.FolderPath;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(entry.Name) ? Path.GetFileName(folder.TrimEnd('\\', '/')) : entry.Name;
            Add(ClaudeConfigService.GetProjectClaudeMdPath(folder), $"CLAUDE.md ({label})", MemoryFileCategory.ProjectInstructions);
            Add(ClaudeConfigService.GetProjectLocalClaudeMdPath(folder), $"CLAUDE.local.md ({label})", MemoryFileCategory.ProjectInstructions);
            Add(Path.Combine(folder, "AGENTS.md"), $"AGENTS.md ({label})", MemoryFileCategory.ProjectInstructions);

            var rulesDir = Path.Combine(folder, ".claude", "rules");
            if (Directory.Exists(rulesDir))
            {
                foreach (var rule in SafeEnumerateMd(rulesDir))
                {
                    Add(rule, $"rules/{Path.GetFileName(rule)} ({label})", MemoryFileCategory.Rules);
                }
            }
        }

        // Auto-memory across all projects (~/.claude/projects/<key>/memory/*.md).
        foreach (var mem in ClaudeConfigService.ListProjectsWithMemory())
        {
            foreach (var md in SafeEnumerateMd(mem.MemoryDirectory))
            {
                var name = Path.GetFileName(md);
                Add(md, $"memory/{name} ({mem.EncodedName})", MemoryFileCategory.AutoMemory);
            }
        }

        return new MemoryAuditResult { Files = files };
    }

    private static IEnumerable<string> SafeEnumerateMd(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*.md");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"MemoryAudit: could not list {dir}: {ex.Message}");
            return [];
        }
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>
    /// Decides whether to surface a "review your memory" nudge now. Fires only when
    /// enabled, the review interval has elapsed since the last nudge, AND the memory
    /// footprint is actually worth reviewing (so users with little/no memory aren't
    /// pestered). Pure function of its inputs — no clock/IO — so it's unit-testable.
    /// </summary>
    public static bool ShouldSuggest(AppSettings settings, MemoryAuditResult audit, DateTimeOffset now)
    {
        if (!settings.EnableMemoryReviewSuggestions)
        {
            return false;
        }

        var intervalDays = Math.Max(1, settings.MemoryReviewIntervalDays);
        if (settings.LastMemoryReviewSuggestionUtc is { } last &&
            now - last < TimeSpan.FromDays(intervalDays))
        {
            return false;
        }

        return audit.TotalTokens >= Math.Max(0, settings.MemoryReviewThresholdTokens);
    }

    /// <summary>
    /// The prompt the user pastes into Claude Code. Includes the concrete audit
    /// findings (largest files) so Claude knows where to start.
    /// </summary>
    public static string BuildReviewPrompt(MemoryAuditResult audit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review and consolidate my Claude Code memory and instruction files. Do all of the following:");
        sb.AppendLine();
        sb.AppendLine("1. Read every file that feeds your context: the global ~/.claude/CLAUDE.md and ~/.claude/AGENTS.md, this project's CLAUDE.md / AGENTS.md / CLAUDE.local.md, any .claude/rules/*.md, and the auto-memory index (MEMORY.md) plus its entry files under ~/.claude/projects/<key>/memory/.");
        sb.AppendLine("2. Find problems: facts duplicated across files, entries that contradict each other, and anything stale or no longer true — verify against the current code/config before trusting an entry.");
        sb.AppendLine("3. Consolidate: merge overlapping entries, delete the wrong/obsolete ones, and tighten wording. Where a durable fact belongs better as a rule, skill, or project setting than as free-form memory, say so (and move it if it's safe).");
        sb.AppendLine("4. Keep it lean — memory should hold facts that aren't already derivable from the code, git history, or existing config.");
        sb.AppendLine("5. Report what you changed and why, and flag anything you were unsure about instead of guessing.");

        if (audit.FileCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"For reference, Sidekick estimates ~{FormatTokens(audit.TotalTokens)} across {audit.FileCount} memory file(s). Largest:");
            foreach (var f in audit.Largest(8))
            {
                sb.AppendLine($"- {f.Path} (~{FormatTokens(f.EstimatedTokens)})");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string FormatTokens(int tokens) =>
        tokens >= 1000
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#}k tokens", tokens / 1000.0)
            : $"{tokens} tokens";
}
