using System.Windows;
using System.Windows.Input;

namespace ClaudeSessionsSidekick;

public enum MissingFolderAction
{
    Cancel,
    CreateFolder
}

public partial class MissingFolderDialog : Window
{
    public MissingFolderAction Action { get; private set; } = MissingFolderAction.Cancel;

    public MissingFolderDialog(string folderPath, Window? owner = null)
    {
        InitializeComponent();
        txtFolder.Text = folderPath;
        if (owner != null)
        {
            Owner = owner;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            Action = MissingFolderAction.Cancel;
            DialogResult = false;
            Close();
            return;
        }

        if (e.Key == Key.Enter)
        {
            Action = MissingFolderAction.CreateFolder;
            DialogResult = true;
            Close();
        }
    }

    private void BtnCreate_Click(object sender, RoutedEventArgs e)
    {
        Action = MissingFolderAction.CreateFolder;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Action = MissingFolderAction.Cancel;
        DialogResult = false;
        Close();
    }

    public static MissingFolderAction Show(string folderPath, Window? owner = null)
    {
        var dialog = new MissingFolderDialog(folderPath, owner);
        dialog.ShowDialog();
        return dialog.Action;
    }
}
