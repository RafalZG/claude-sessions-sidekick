using System.Text.RegularExpressions;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// The template language for the Claude Code status line builder: a plain
/// string with placeholders like <c>{project}</c> or <c>{ctx}</c>. The token
/// list, colors and thresholds below are the single source both consumers
/// draw from: the colored preview in StatusLineWindow and the generated
/// PowerShell script (see <see cref="StatusLineScriptGenerator"/>).
/// </summary>
public static class StatusLineTemplate
{
    public sealed record Placeholder(string Token, string Description, string SampleValue);

    /// <summary>
    /// Percent values are bare numbers so the user decides the suffix
    /// ("9% ctx", "5h 24%"). Empty string means the field is unavailable in
    /// the current session (e.g. no rate limits on API-key billing).
    /// </summary>
    public static readonly IReadOnlyList<Placeholder> Placeholders =
    [
        new("{project}", "Project folder name", "ExergyERP_Dev"),
        new("{branch}", "Git branch", "master"),
        new("{worktree}", "Worktree name (empty outside a worktree)", "wt-feature"),
        new("{model}", "Model name", "Fable 5"),
        new("{ctx}", "Context used (number, add % yourself)", "9"),
        new("{cost}", "Session cost in USD", "$0.48"),
        new("{lines}", "Lines added/removed", "+156/-23"),
        new("{version}", "Claude Code version", "2.1.90"),
        new("{effort}", "Effort level", "high"),
        new("{5h}", "5-hour limit used (number)", "24"),
        new("{7d}", "Weekly limit used (number)", "41"),
    ];

    public const int MaxTemplateLength = 500;

    /// <summary>
    /// Shared coloring for block values: the ANSI SGR code the generated
    /// script emits and the matching hex the preview paints with. Tokens
    /// without an entry render in the terminal's default color. Percent
    /// tokens are colored dynamically — see <see cref="PercentColor"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Ansi, string Hex)> BlockColors =
        new Dictionary<string, (string Ansi, string Hex)>
        {
            ["{project}"] = ("36", "#79C0FF"),
            ["{worktree}"] = ("33", "#F2CC60"),
            ["{branch}"] = ("32", "#7EE787"),
            ["{model}"] = ("35", "#D2A8FF"),
            ["{version}"] = ("90", "#8B949E"),
            ["{effort}"] = ("90", "#8B949E"),
        };

    public static readonly IReadOnlyList<string> PercentTokens = ["{ctx}", "{5h}", "{7d}"];

    public const int PercentWarnThreshold = 50;
    public const int PercentCritThreshold = 80;

    public static (string Ansi, string Hex) PercentColor(double value)
    {
        if (value >= PercentCritThreshold)
        {
            return ("31", "#F28B82");
        }
        if (value >= PercentWarnThreshold)
        {
            return ("33", "#F2CC60");
        }
        return ("32", "#7EE787");
    }

    public static readonly string DefaultTemplate = "{project} {worktree} ({branch}) [{model}] {ctx}% ctx";

    public static readonly IReadOnlyList<(string Name, string Template)> Presets =
    [
        ("Project + branch + model + context", DefaultTemplate),
        ("With usage limits", "{project} {worktree} ({branch}) [{model}] {ctx}% ctx | 5h {5h}% | 7d {7d}%"),
        ("With session cost", "{project} {worktree} ({branch}) [{model}] {ctx}% ctx | {cost} | {lines}"),
    ];

    /// <summary>
    /// The executable specification of what the generated PowerShell script
    /// does at runtime: substitute every known placeholder, then tidy up what
    /// missing values leave behind — "()" / "[]" shells and doubled spaces.
    /// Not called by the preview (which builds colored runs from the same
    /// token list); its tests pin the semantics the script must mirror.
    /// Unknown tokens are left visible on purpose — a typo should show up,
    /// not vanish silently.
    /// </summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        var result = template;
        foreach (var placeholder in Placeholders)
        {
            values.TryGetValue(placeholder.Token, out var value);
            result = result.Replace(placeholder.Token, value ?? "");
        }

        result = Regex.Replace(result, @"\(\s*\)|\[\s*\]", "");
        result = Regex.Replace(result, @"\s{2,}", " ");
        var trimmed = result.Trim();
        return trimmed;
    }

    /// <summary>Sample values for the live preview in the builder window.</summary>
    public static Dictionary<string, string> SampleValues()
    {
        var samples = new Dictionary<string, string>();
        foreach (var placeholder in Placeholders)
        {
            samples[placeholder.Token] = placeholder.SampleValue;
        }
        return samples;
    }
}
