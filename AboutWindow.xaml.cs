using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class AboutWindow : Window
{
    private const string GitHubRepo = "RafalZG/claude-sessions-sidekick";
    private const string GitHubUrl = $"https://github.com/{GitHubRepo}";
    private const string IssuesUrl = $"https://github.com/{GitHubRepo}/issues/new";
    private const string ReleasesApiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";

    private static readonly Version CurrentVersion =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public AboutWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, _) => DragMove();
        txtVersion.Text = $"Version {CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void LnkGitHub_Click(object sender, MouseButtonEventArgs e)
    {
        OpenUrl(GitHubUrl);
    }

    private void LnkIssues_Click(object sender, MouseButtonEventArgs e)
    {
        OpenUrl(IssuesUrl);
    }

    private async void LnkCheckUpdate_Click(object sender, MouseButtonEventArgs e)
    {
        txtUpdateStatus.Text = "Checking...";
        lnkCheckUpdate.IsEnabled = false;

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "ClaudeSessionsSidekick");
            client.Timeout = TimeSpan.FromSeconds(10);

            var response = await client.GetStringAsync(ReleasesApiUrl);
            using var doc = JsonDocument.Parse(response);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";

            // Parse version from tag (e.g. "v1.2.0" -> "1.2.0")
            var versionStr = tagName.TrimStart('v', 'V');
            if (Version.TryParse(versionStr, out var latestVersion) && latestVersion > CurrentVersion)
            {
                var releaseUrl = doc.RootElement.GetProperty("html_url").GetString() ?? GitHubUrl;
                txtUpdateStatus.Text = $"New version {versionStr} available!";
                txtUpdateStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0xE0, 0x60));
                txtUpdateStatus.Cursor = Cursors.Hand;
                txtUpdateStatus.TextDecorations = TextDecorations.Underline;
                txtUpdateStatus.MouseLeftButtonDown += (_, _) => OpenUrl(releaseUrl);
            }
            else
            {
                txtUpdateStatus.Text = "You're up to date!";
            }
        }
        catch
        {
            txtUpdateStatus.Text = "Could not check for updates";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

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
