using System.Windows;
using System.Windows.Input;

namespace ClaudeSessionsSidekick;

public partial class TopicEditorDialog : Window
{
    public string? TopicText { get; private set; }

    public TopicEditorDialog(string sessionLabel, string? existingTopic, Window? owner = null)
    {
        InitializeComponent();
        txtSessionLabel.Text = sessionLabel;
        txtTopic.Text = existingTopic ?? "";
        if (owner != null)
        {
            Owner = owner;
        }
        Loaded += (_, _) =>
        {
            txtTopic.Focus();
            txtTopic.SelectAll();
        };
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        Save();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save()
    {
        var text = txtTopic.Text?.Trim();
        TopicText = string.IsNullOrEmpty(text) ? null : text;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Shows the dialog and returns (saved, topic). On Save with an empty
    /// box the second element is null — the caller should treat that as
    /// "clear the override" and let the original fallback chain take over.
    /// </summary>
    public static (bool Saved, string? Topic) Show(string sessionLabel, string? existingTopic, Window? owner = null)
    {
        var dialog = new TopicEditorDialog(sessionLabel, existingTopic, owner);
        var result = dialog.ShowDialog();
        return (result == true, dialog.TopicText);
    }
}
