using System.Windows;
using System.Windows.Input;

namespace ClaudeSessionsSidekick;

public partial class ConfirmDialog : Window
{
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

        // Future-proof: if a focused TextBox is in the dialog, let it handle Enter.
        // Currently no textboxes here, but this prevents accidental confirms later.
        if (e.Key == Key.Enter && e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }
    }

    public bool Confirmed { get; private set; }

    public ConfirmDialog(string title, string message, string confirmText = "Delete", Window? owner = null)
    {
        InitializeComponent();
        txtTitle.Text = title;
        txtMessage.Text = message;
        btnConfirm.Content = confirmText;
        if (owner != null)
        {
            Owner = owner;
        }
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static bool Show(string title, string message, string confirmText = "Delete", Window? owner = null)
    {
        var dialog = new ConfirmDialog(title, message, confirmText, owner);
        dialog.ShowDialog();
        return dialog.Confirmed;
    }
}
