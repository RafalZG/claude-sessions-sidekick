using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class AboutWindow : Window
{
    private const string GitHubRepo = "RafalZG/claude-sessions-sidekick";
    private const string GitHubUrl = $"https://github.com/{GitHubRepo}";
    private const string IssuesUrl = $"https://github.com/{GitHubRepo}/issues/new";
    private const string ReleasesUrl = $"https://github.com/{GitHubRepo}/releases";
    private const string ChangelogUrl = $"https://github.com/{GitHubRepo}/blob/main/CHANGELOG.md";
    private const string ReadmeUrl = $"https://github.com/{GitHubRepo}#readme";
    private const string VibeCodingUrl = "https://en.wikipedia.org/wiki/Vibe_coding";

    public AboutWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, _) => DragMove();
        txtVersion.Text = "Version " + ResolveDisplayVersion();
    }

    /// <summary>
    /// Velopack-installed builds know the real semver (incl. pre-release tags
    /// like <c>1.0.0-rc2</c>) via <see cref="UpdateService.InstalledVersion"/>.
    /// Dev builds (<c>dotnet run</c>) fall back to the assembly file version,
    /// which is set in csproj <c>&lt;Version&gt;</c>.
    /// </summary>
    private static string ResolveDisplayVersion()
    {
        var velopackVersion = new UpdateService().InstalledVersion;
        if (!string.IsNullOrEmpty(velopackVersion))
        {
            return velopackVersion;
        }
        // AssemblyInformationalVersion picks up SemVer pre-release suffixes
        // (e.g. "1.0.0-rc2+abc123") set via -p:Version on dotnet publish;
        // falls back to AssemblyVersion if not present.
        var infoAttr = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (infoAttr != null && !string.IsNullOrEmpty(infoAttr.InformationalVersion))
        {
            // Strip the optional `+commitSha` build metadata for display.
            var v = infoAttr.InformationalVersion;
            var plusIdx = v.IndexOf('+');
            return plusIdx >= 0 ? v[..plusIdx] : v;
        }
        var asm = Assembly.GetExecutingAssembly().GetName().Version;
        return asm?.ToString(3) ?? "1.0.0";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void LnkGitHub_Click(object sender, MouseButtonEventArgs e) => OpenUrl(GitHubUrl);
    private void LnkIssues_Click(object sender, MouseButtonEventArgs e) => OpenUrl(IssuesUrl);
    private void LnkChangelog_Click(object sender, MouseButtonEventArgs e) => OpenUrl(ChangelogUrl);
    private void LnkReadme_Click(object sender, MouseButtonEventArgs e) => OpenUrl(ReadmeUrl);
    private void LnkReleases_Click(object sender, MouseButtonEventArgs e) => OpenUrl(ReleasesUrl);

    private void LnkVibeCoding_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(VibeCodingUrl);
        if (e is RequestNavigateEventArgs nav)
        {
            nav.Handled = true;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Browser may not be available
        }
    }
}
