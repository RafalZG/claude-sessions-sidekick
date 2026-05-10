using System.Windows;
using System.Windows.Input;

namespace ClaudeSessionsSidekick;

public partial class NoteEditorDialog : Window
{
    public string? NoteText { get; private set; }

    public NoteEditorDialog(string sessionLabel, string? existingNote, Window? owner = null)
    {
        InitializeComponent();
        txtSessionLabel.Text = sessionLabel;
        txtNote.Text = existingNote ?? "";
        if (owner != null)
        {
            Owner = owner;
        }
        Loaded += (_, _) =>
        {
            txtNote.Focus();
            txtNote.CaretIndex = txtNote.Text.Length;
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
            return;
        }

        // Ctrl+Enter saves; plain Enter inserts a newline (AcceptsReturn=true).
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Save();
            e.Handled = true;
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
        // Trim and treat empty as "clear note".
        var text = txtNote.Text?.Trim();
        NoteText = string.IsNullOrEmpty(text) ? null : text;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Shows the dialog and returns the new note text on Save (null = clear),
    /// or the original value on Cancel via the second tuple element.
    /// </summary>
    public static (bool Saved, string? Note) Show(string sessionLabel, string? existingNote, Window? owner = null)
    {
        var dialog = new NoteEditorDialog(sessionLabel, existingNote, owner);
        var result = dialog.ShowDialog();
        return (result == true, dialog.NoteText);
    }
}
