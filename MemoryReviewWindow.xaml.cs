using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

/// <summary>
/// Shows the current Claude Code memory footprint and hands the user a ready prompt
/// to paste into Claude Code so it can consolidate the files itself. Opened from the
/// periodic "review your memory" nudge or on demand from the tray.
/// </summary>
public partial class MemoryReviewWindow : Window
{
    private readonly string _prompt;
    private readonly Action _openMemoryManager;
    private readonly Action _dontRemind;
    private readonly Action _remindLater;

    // Files at or above this rough token estimate are highlighted as worth trimming.
    private const int LargeFileTokens = 6000;

    public MemoryReviewWindow(MemoryAuditResult audit, Action openMemoryManager, Action dontRemind, Action remindLater)
    {
        InitializeComponent();

        _openMemoryManager = openMemoryManager;
        _dontRemind = dontRemind;
        _remindLater = remindLater;
        _prompt = MemoryAuditService.BuildReviewPrompt(audit);

        txtSummary.Text = audit.FileCount == 0
            ? "No memory files found."
            : $"Your Claude Code memory is about {MemoryAuditService.FormatTokens(audit.TotalTokens)} across {audit.FileCount} file(s). " +
              "Keeping it lean helps the model stay sharp.";

        lstFiles.ItemsSource = audit.Files
            .OrderByDescending(f => f.EstimatedTokens)
            .Select(f => new Row(f))
            .ToList();

        txtPrompt.Text = _prompt;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_prompt);
            txtCopied.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"MemoryReview: copy failed: {ex.Message}");
        }
    }

    private void BtnOpenMemoryManager_Click(object sender, RoutedEventArgs e)
    {
        _openMemoryManager();
        Close();
    }

    private void BtnDontRemind_Click(object sender, RoutedEventArgs e)
    {
        _dontRemind();
        Close();
    }

    private void BtnRemindLater_Click(object sender, RoutedEventArgs e)
    {
        _remindLater();
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    /// <summary>Row view-model for the file list.</summary>
    private sealed class Row
    {
        public Row(MemoryAuditFile f)
        {
            Path = f.Path;
            DisplayName = f.DisplayName;
            TokensDisplay = MemoryAuditService.FormatTokens(f.EstimatedTokens).Replace(" tokens", "");
            Icon = f.Category switch
            {
                MemoryFileCategory.GlobalInstructions => "⌂",  // house — global
                MemoryFileCategory.ProjectInstructions => "■", // square — project
                MemoryFileCategory.Rules => "§",               // section — rules
                MemoryFileCategory.AutoMemory => "◉",          // fisheye — auto-memory
                _ => "•",
            };
            TokensBrush = f.EstimatedTokens >= LargeFileTokens
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xA0, 0x30))  // amber — large
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x90, 0xA8)); // muted
        }

        public string Path { get; }
        public string DisplayName { get; }
        public string TokensDisplay { get; }
        public string Icon { get; }
        public System.Windows.Media.Brush TokensBrush { get; }
    }
}
