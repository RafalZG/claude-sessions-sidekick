using System.Windows;
using System.Windows.Input;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class McpEditWindow : Window
{
    public McpServerEntry? Result { get; private set; }
    private readonly string? _originalName;

    public McpEditWindow(McpServerEntry? existing = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        if (existing != null)
        {
            _originalName = existing.Name;
            Title = $"Edit MCP Server — {existing.Name}";
            txtName.Text = existing.Name;

            if (!string.IsNullOrEmpty(existing.Url))
            {
                rbHttp.IsChecked = true;
                txtUrl.Text = existing.Url;
            }
            else
            {
                rbStdio.IsChecked = true;
                txtCommand.Text = existing.Command;
                txtArgs.Text = existing.Args;
            }

            if (existing.EnvVars.Count > 0)
            {
                txtEnv.Text = string.Join("\n", existing.EnvVars.Select(kv => $"{kv.Key}={kv.Value}"));
            }

            if (existing.Headers.Count > 0)
            {
                txtHeaders.Text = string.Join("\n", existing.Headers.Select(kv => $"{kv.Key}={kv.Value}"));
            }
        }
        else
        {
            Title = "Add MCP Server";
        }

        UpdateFieldVisibility();
    }

    private void TransportType_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFieldVisibility();
    }

    private void UpdateFieldVisibility()
    {
        if (lblCommand == null)
        {
            return;
        }

        var isStdio = rbStdio.IsChecked == true;

        lblCommand.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        txtCommand.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        lblArgs.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;
        txtArgs.Visibility = isStdio ? Visibility.Visible : Visibility.Collapsed;

        lblUrl.Visibility = isStdio ? Visibility.Collapsed : Visibility.Visible;
        txtUrl.Visibility = isStdio ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var name = txtName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Please enter a server name.", "Validation", MessageBoxButton.OK);
            return;
        }

        var server = new McpServerEntry { Name = name };

        if (rbHttp.IsChecked == true)
        {
            var url = txtUrl.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Please enter a URL.", "Validation", MessageBoxButton.OK);
                return;
            }
            server.Url = url;
            server.TransportType = "http";
        }
        else
        {
            var cmd = txtCommand.Text.Trim();
            if (string.IsNullOrEmpty(cmd))
            {
                MessageBox.Show("Please enter a command.", "Validation", MessageBoxButton.OK);
                return;
            }
            server.Command = cmd;
            server.Args = txtArgs.Text.Trim();
            server.TransportType = "stdio";
        }

        // Parse env vars
        foreach (var line in txtEnv.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                server.EnvVars[line[..eqIdx].Trim()] = line[(eqIdx + 1)..].Trim();
            }
        }

        // Parse headers
        foreach (var line in txtHeaders.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
            {
                server.Headers[line[..eqIdx].Trim()] = line[(eqIdx + 1)..].Trim();
            }
        }

        try
        {
            McpConfigService.SaveServer(server, _originalName);
            Result = server;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save: {ex.Message}", "Error", MessageBoxButton.OK);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }
}
