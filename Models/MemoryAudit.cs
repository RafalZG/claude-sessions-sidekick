namespace ClaudeSessionsSidekick.Models;

/// <summary>
/// Where a memory/instruction file sits in Claude Code's context so the audit can
/// group and label it.
/// </summary>
public enum MemoryFileCategory
{
    GlobalInstructions, // ~/.claude/CLAUDE.md, ~/.claude/AGENTS.md
    ProjectInstructions, // <project>/CLAUDE.md, AGENTS.md, CLAUDE.local.md
    Rules,               // <project>/.claude/rules/*.md
    AutoMemory,          // ~/.claude/projects/<key>/memory/*.md
}

/// <summary>One file that feeds Claude Code's context, with a rough token estimate.</summary>
public class MemoryAuditFile
{
    public required string Path { get; init; }

    /// <summary>Short, human-friendly label for the UI (not the full path).</summary>
    public required string DisplayName { get; init; }

    public MemoryFileCategory Category { get; init; }

    public long Bytes { get; init; }

    /// <summary>
    /// Rough token estimate (~bytes/4). This is deliberately approximate — it exists
    /// to spot bloated files relative to each other, not to bill anyone to the token.
    /// </summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>The full memory footprint of a Claude Code setup at a point in time.</summary>
public class MemoryAuditResult
{
    public List<MemoryAuditFile> Files { get; init; } = [];

    public int TotalTokens => Files.Sum(f => f.EstimatedTokens);

    public int FileCount => Files.Count;

    public IReadOnlyList<MemoryAuditFile> Largest(int n) =>
        Files.OrderByDescending(f => f.EstimatedTokens).Take(n).ToList();
}
