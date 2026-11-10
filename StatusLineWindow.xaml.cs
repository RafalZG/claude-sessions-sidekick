using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class StatusLineWindow : Window
{
    // MainWindow's live AppSettings instance — mutated in place and saved, the
    // same pattern as SettingsWindow. Loading a fresh copy here instead would
    // get clobbered the next time MainWindow whole-object-saves its own copy
    // (e.g. the throttled update check persisting LastUpdateCheckUtc).
    private readonly AppSettings _appSettings;

    public StatusLineWindow(AppSettings appSettings)
    {
        _appSettings = appSettings;
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        BuildPresetButtons();
        BuildBlockButtons();

        txtTemplate.Text = _appSettings.StatusLineTemplate ?? StatusLineTemplate.DefaultTemplate;
        RefreshStatus();
    }

    private void BuildPresetButtons()
    {
        foreach (var preset in StatusLineTemplate.Presets)
        {
            var capturedTemplate = preset.Template;
            var button = new Button
            {
                Content = preset.Name,
                Style = (Style)FindResource("PresetButton"),
                ToolTip = preset.Template
            };
            button.Click += (_, _) =>
            {
                txtTemplate.Text = capturedTemplate;
                txtTemplate.CaretIndex = txtTemplate.Text.Length;
                txtTemplate.Focus();
            };
            pnlPresets.Children.Add(button);
        }
    }

    private void BuildBlockButtons()
    {
        foreach (var placeholder in StatusLineTemplate.Placeholders)
        {
            var capturedToken = placeholder.Token;
            var button = new Button
            {
                Content = placeholder.Token,
                Style = (Style)FindResource("BlockButton"),
                ToolTip = $"{placeholder.Description} — e.g. {placeholder.SampleValue}"
            };
            button.Click += (_, _) => InsertAtCaret(capturedToken);
            pnlBlocks.Children.Add(button);
        }
    }

    private void InsertAtCaret(string token)
    {
        // MaxLength on the TextBox only restricts typing; programmatic
        // SelectedText assignment bypasses it, so enforce the cap here too.
        var lengthAfterInsert = txtTemplate.Text.Length - txtTemplate.SelectionLength + token.Length;
        if (lengthAfterInsert > StatusLineTemplate.MaxTemplateLength)
        {
            return;
        }
        var caret = txtTemplate.CaretIndex;
        txtTemplate.SelectedText = token;
        txtTemplate.SelectionLength = 0;
        txtTemplate.CaretIndex = caret + token.Length;
        txtTemplate.Focus();
    }

    private static readonly System.Windows.Media.Brush PreviewDefaultBrush = BrushFromHex("#C0C8D8");

    private static System.Windows.Media.Brush BrushFromHex(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush PreviewBrushForToken(string token, string sampleValue)
    {
        if (StatusLineTemplate.PercentTokens.Contains(token)
            && double.TryParse(sampleValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
        {
            return BrushFromHex(StatusLineTemplate.PercentColor(pct).Hex);
        }
        if (StatusLineTemplate.BlockColors.TryGetValue(token, out var color))
        {
            return BrushFromHex(color.Hex);
        }
        return PreviewDefaultBrush;
    }

    /// <summary>
    /// Mirrors the generated script's coloring: block values get their color,
    /// the literal template text stays default. Sample values are all
    /// non-empty, so the empty-()/[]-cleanup never applies to the preview.
    /// </summary>
    private void TxtTemplate_TextChanged(object sender, TextChangedEventArgs e)
    {
        txtPreview.Inlines.Clear();
        var template = txtTemplate.Text;
        if (string.IsNullOrWhiteSpace(template))
        {
            var emptyRun = new Run("(empty)") { Foreground = PreviewDefaultBrush };
            txtPreview.Inlines.Add(emptyRun);
            return;
        }

        var position = 0;
        while (position < template.Length)
        {
            var nextIndex = -1;
            StatusLineTemplate.Placeholder? nextPlaceholder = null;
            foreach (var placeholder in StatusLineTemplate.Placeholders)
            {
                var index = template.IndexOf(placeholder.Token, position, StringComparison.Ordinal);
                if (index >= 0 && (nextIndex == -1 || index < nextIndex))
                {
                    nextIndex = index;
                    nextPlaceholder = placeholder;
                }
            }

            if (nextPlaceholder == null)
            {
                var tailRun = new Run(template[position..]) { Foreground = PreviewDefaultBrush };
                txtPreview.Inlines.Add(tailRun);
                break;
            }

            if (nextIndex > position)
            {
                var literalRun = new Run(template[position..nextIndex]) { Foreground = PreviewDefaultBrush };
                txtPreview.Inlines.Add(literalRun);
            }
            var valueRun = new Run(nextPlaceholder.SampleValue)
            {
                Foreground = PreviewBrushForToken(nextPlaceholder.Token, nextPlaceholder.SampleValue)
            };
            txtPreview.Inlines.Add(valueRun);
            position = nextIndex + nextPlaceholder.Token.Length;
        }
    }

    private void RefreshStatus()
    {
        var status = StatusLineService.GetStatus();
        switch (status.State)
        {
            case StatusLineState.Ours:
                txtStatus.Text = "Current: set by Sidekick. Apply overwrites it, Remove clears it.";
                break;
            case StatusLineState.Foreign:
                txtStatus.Text = "Current: a status line is already configured outside Sidekick:\n" +
                                 $"{status.CurrentCommand}\n" +
                                 "Apply will replace it (you'll be asked first).";
                break;
            case StatusLineState.Unreadable:
                txtStatus.Text = "Current: ~/.claude/settings.json exists but couldn't be read — " +
                                 "fix or remove the file first; Apply and Remove won't touch it.";
                break;
            default:
                txtStatus.Text = "Current: no status line configured.";
                break;
        }
        btnRemove.IsEnabled = status.State is StatusLineState.Ours or StatusLineState.Foreign;
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        var template = txtTemplate.Text.Trim();
        if (template.Length == 0)
        {
            ConfirmDialog.Show("Empty template",
                "Add at least one block before applying.", "OK", this);
            return;
        }

        var status = StatusLineService.GetStatus();
        if (status.State == StatusLineState.Foreign)
        {
            var ok = ConfirmDialog.Show(
                "Replace existing status line?",
                "settings.json already has a status line that Sidekick didn't create:\n\n" +
                $"{status.CurrentCommand}\n\n" +
                "Replace it with the Sidekick one? (A .bak backup of settings.json is kept.)",
                "Replace",
                this);
            if (!ok)
            {
                return;
            }
        }

        try
        {
            StatusLineService.Apply(template);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"StatusLineWindow: apply failed: {ex.Message}");
            ConfirmDialog.Show("Apply failed", ex.Message, "OK", this);
            return;
        }

        _appSettings.StatusLineTemplate = template;
        SettingsService.Save(_appSettings);

        RefreshStatus();
        txtStatus.Text = "Applied.\n" + txtStatus.Text;
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        var status = StatusLineService.GetStatus();
        var message = status.State == StatusLineState.Foreign
            ? "This status line was configured outside Sidekick. Remove it anyway? " +
              "(A .bak backup of settings.json is kept.)"
            : "Remove the status line from settings.json and delete the generated script?";
        var ok = ConfirmDialog.Show("Remove status line?", message, "Remove", this);
        if (!ok)
        {
            return;
        }

        try
        {
            StatusLineService.Remove();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"StatusLineWindow: remove failed: {ex.Message}");
            ConfirmDialog.Show("Remove failed", ex.Message, "OK", this);
            return;
        }
        RefreshStatus();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
