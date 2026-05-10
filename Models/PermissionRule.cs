using System.Text.Json.Serialization;

namespace ClaudeSessionsSidekick.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PermissionScope
{
    Allow,
    Deny,
    Ask
}

/// <summary>
/// One Claude Code permission rule, e.g. "Bash(npm run *)" or "Read".
/// Tool is the part before the parenthesis; Pattern is what's inside (or null
/// for a "match-all" rule like bare "Bash" or "Read").
/// </summary>
public class PermissionRule
{
    public PermissionScope Scope { get; set; }
    public string Tool { get; set; } = "";
    public string? Pattern { get; set; }

    /// <summary>Round-trips back to the wire format Claude Code stores in settings.json.</summary>
    public string RuleString =>
        string.IsNullOrEmpty(Pattern) ? Tool : $"{Tool}({Pattern})";

    /// <summary>
    /// Parses a Claude Code permission rule string. Returns null for malformed input
    /// (caller should log and skip).
    /// </summary>
    public static PermissionRule? Parse(string raw, PermissionScope scope)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim();

        var openParen = raw.IndexOf('(');
        if (openParen < 0)
        {
            // Bare tool name = match-all
            return new PermissionRule { Scope = scope, Tool = raw, Pattern = null };
        }

        if (!raw.EndsWith(')'))
        {
            return null;
        }

        var tool = raw[..openParen].Trim();
        var pattern = raw[(openParen + 1)..^1];

        if (string.IsNullOrEmpty(tool))
        {
            return null;
        }

        return new PermissionRule { Scope = scope, Tool = tool, Pattern = pattern };
    }

    /// <summary>
    /// Suggests a generalized version of this rule's pattern, or null if it's already
    /// general or can't be safely generalized. E.g. Bash(npm install foo) → "npm *".
    /// Only generalizes simple "command args..." patterns. Skips quoted/absolute-path
    /// executables - splitting "C:\Program Files\..." on the first space would
    /// produce nonsense like '"C:\Program *'.
    /// </summary>
    public string? SuggestGeneralizedPattern()
    {
        if (Tool != "Bash" || string.IsNullOrEmpty(Pattern))
        {
            return null;
        }

        // Already wildcarded?
        var trimmed = Pattern.TrimEnd();
        if (trimmed == "*" || trimmed.EndsWith(" *") || trimmed.EndsWith(":*"))
        {
            return null;
        }

        // Skip quoted-path / absolute-path patterns - can't safely tokenize them
        // and they're so specific that any generalization would be misleading.
        if (Pattern.StartsWith('"') || Pattern.StartsWith('\'') ||
            Pattern.StartsWith('/') || Pattern.StartsWith('\\') ||
            Pattern.Contains('"') || Pattern.Contains('\''))
        {
            return null;
        }

        // Take the first whitespace-separated token (the command name)
        var firstSpace = Pattern.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        var command = Pattern[..firstSpace];

        // First token must look like a plain executable name - reject anything
        // with path separators, drive letters, env-var assignment, or shell metacharacters
        if (command.Contains('\\') || command.Contains('/') || command.Contains(':') ||
            command.Contains('(') || command.Contains(')') || command.Contains('|') ||
            command.Contains('&') || command.Contains(';') || command.Contains('='))
        {
            return null;
        }

        return $"{command} *";
    }

    /// <summary>
    /// Returns true if `other` is a strictly broader rule in the same scope that fully
    /// covers this one. Used to flag redundant rules. Conservative on purpose: only
    /// handles exact tool match plus simple Bash prefix wildcards. Glob-based file
    /// rules (Read/Edit/Write) are intentionally not analyzed - too easy to be wrong.
    /// </summary>
    public bool IsCoveredBy(PermissionRule other)
    {
        if (ReferenceEquals(other, this))
        {
            return false;
        }

        if (other.Scope != Scope)
        {
            return false;
        }

        if (!string.Equals(other.Tool, Tool, StringComparison.Ordinal))
        {
            return false;
        }

        // "other" is a bare tool name or "*" pattern - covers everything for this tool
        if (string.IsNullOrEmpty(other.Pattern) || other.Pattern == "*")
        {
            // ...unless we are also the bare/star rule (don't flag self vs equal-broad)
            return !(string.IsNullOrEmpty(Pattern) || Pattern == "*");
        }

        // Identical patterns aren't redundant - they're duplicates (different concept)
        if (string.Equals(other.Pattern, Pattern, StringComparison.Ordinal))
        {
            return false;
        }

        // Bash prefix wildcard: "npm *" covers "npm install" / "npm install foo"
        if (Tool == "Bash" && other.Pattern.EndsWith(" *") && !string.IsNullOrEmpty(Pattern))
        {
            var prefix = other.Pattern[..^2]; // strip " *"
            if (Pattern == prefix ||
                Pattern.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
