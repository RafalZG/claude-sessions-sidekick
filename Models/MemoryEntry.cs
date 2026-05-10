namespace ClaudeSessionsSidekick.Models;

/// <summary>
/// One Claude Code auto-memory entry - a .md file with frontmatter (name, description,
/// type) and a body. Lives in ~/.claude/projects/&lt;encoded&gt;/memory/.
/// </summary>
public class MemoryEntry
{
    /// <summary>Absolute path to the .md file.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Just the filename (e.g. "user_role.md").</summary>
    public string FileName { get; set; } = "";

    /// <summary>From frontmatter 'name' field.</summary>
    public string Name { get; set; } = "";

    /// <summary>From frontmatter 'description' field.</summary>
    public string Description { get; set; } = "";

    /// <summary>From frontmatter 'type' field (user / feedback / project / reference).</summary>
    public string Type { get; set; } = "";

    /// <summary>Everything after the closing --- of the frontmatter.</summary>
    public string Body { get; set; } = "";

    /// <summary>From frontmatter 'tools' field (agents only, e.g. ["mcp__nuget__*", "Bash"]).</summary>
    public string Tools { get; set; } = "";

    /// <summary>Full file content including frontmatter, as loaded from disk.</summary>
    public string RawContent { get; set; } = "";

    /// <summary>Last modification time of the file on disk.</summary>
    public DateTime LastModified { get; set; }

    public string LastModifiedDisplay => LastModified == DateTime.MinValue
        ? ""
        : LastModified.ToString("yyyy-MM-dd HH:mm");
}
