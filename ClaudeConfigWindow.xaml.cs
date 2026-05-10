using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class ClaudeConfigWindow : Window
{
    private readonly List<QuickLaunchEntry> _projects;
    private readonly List<ScopeOption> _scopes = new();
    private string? _currentFilePath;
    private string _loadedContent = "";
    private bool _isDirty;
    private bool _suppressDirty;

    private List<MemoryEntry> _allMemEntries = new();
    private bool _suppressMemSelection;

    public ClaudeConfigWindow(List<QuickLaunchEntry> quickLaunchProjects)
    {
        InitializeComponent();
        Services.DarkTitleBar.Apply(this);

        _projects = quickLaunchProjects;
        BuildScopeList();
        BuildMemoryProjectList();

        cmbScope.SelectedIndex = 0;
        if (cmbMemProject.Items.Count > 0)
        {
            cmbMemProject.SelectedIndex = 0;
        }

        Closing += OnWindowClosing;
    }

    // ---- CLAUDE.md tab ----

    private void BuildScopeList()
    {
        // Terminology matches Claude Code's /memory command:
        //   User memory    = ~/.claude/CLAUDE.md          (cross-project)
        //   Project memory = <project>/CLAUDE.md          (committed to repo)
        //   Project local  = <project>/CLAUDE.local.md    (gitignored, per-developer)
        _scopes.Add(new ScopeOption
        {
            DisplayName = "User memory  (~/.claude/CLAUDE.md)",
            FilePath = ClaudeConfigService.GlobalClaudeMdPath,
        });

        foreach (var p in _projects.Where(p => !string.IsNullOrWhiteSpace(p.FolderPath))
                                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            _scopes.Add(new ScopeOption
            {
                DisplayName = $"Project memory: {p.Name}  (CLAUDE.md)",
                FilePath = ClaudeConfigService.GetProjectClaudeMdPath(p.FolderPath),
            });
            _scopes.Add(new ScopeOption
            {
                DisplayName = $"Project local: {p.Name}  (CLAUDE.local.md)",
                FilePath = ClaudeConfigService.GetProjectLocalClaudeMdPath(p.FolderPath),
            });
        }

        cmbScope.ItemsSource = _scopes;
        cmbScope.DisplayMemberPath = nameof(ScopeOption.DisplayName);
    }

    private void LoadCurrentClaudeMd()
    {
        if (cmbScope.SelectedItem is not ScopeOption scope)
        {
            return;
        }

        _currentFilePath = scope.FilePath;
        _loadedContent = ClaudeConfigService.LoadText(scope.FilePath);

        _suppressDirty = true;
        txtContent.Text = _loadedContent;
        _suppressDirty = false;

        txtFilePath.Text = scope.FilePath;
        _isDirty = false;
        btnSaveClaudeMd.IsEnabled = false;
        UpdateCharCount();
        UpdateStatus();
    }

    private void UpdateCharCount()
    {
        var len = txtContent.Text?.Length ?? 0;
        var exists = _currentFilePath != null && File.Exists(_currentFilePath);
        txtCharCount.Text = exists
            ? $"{len:N0} chars"
            : $"{len:N0} chars  (new file)";
    }

    private void UpdateStatus()
    {
        var dirty = _isDirty ? "  *" : "";
        txtStatus.Text = $"CLAUDE.md: {_currentFilePath ?? "none"}{dirty}";
    }

    private void CmbScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDirty)
        {
            var ok = ConfirmDialog.Show("Discard unsaved changes?",
                "You have unsaved CLAUDE.md changes. Discard them and switch scope?",
                "Discard", this);
            if (!ok)
            {
                _suppressDirty = true;
                var prev = _scopes.FirstOrDefault(s => s.FilePath == _currentFilePath);
                if (prev != null)
                {
                    cmbScope.SelectedItem = prev;
                }
                _suppressDirty = false;
                return;
            }
        }

        LoadCurrentClaudeMd();
    }

    private void TxtContent_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCharCount();
        if (_suppressDirty)
        {
            return;
        }
        if (txtContent.Text != _loadedContent)
        {
            if (!_isDirty)
            {
                _isDirty = true;
                btnSaveClaudeMd.IsEnabled = true;
                UpdateStatus();
            }
        }
    }

    private void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        if (_isDirty)
        {
            var ok = ConfirmDialog.Show("Discard changes?",
                "Reload from disk and discard unsaved changes?",
                "Discard", this);
            if (!ok)
            {
                return;
            }
        }
        LoadCurrentClaudeMd();
    }

    private void BtnSaveClaudeMd_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFilePath == null)
        {
            return;
        }

        try
        {
            ClaudeConfigService.SaveText(_currentFilePath, txtContent.Text ?? "");
            _loadedContent = txtContent.Text ?? "";
            _isDirty = false;
            btnSaveClaudeMd.IsEnabled = false;
            UpdateCharCount();
            UpdateStatus();
            ConfirmDialog.Show("CLAUDE.md saved",
                $"Saved to:\n{_currentFilePath}\n\nBackup of previous content at:\n{_currentFilePath}.bak",
                "OK", this);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to save CLAUDE.md", ex);
            ConfirmDialog.Show("Save failed", $"Couldn't save:\n{ex.Message}", "OK", this);
        }
    }

    private void TxtFilePath_Click(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
        {
            return;
        }

        try
        {
            if (!File.Exists(_currentFilePath))
            {
                ConfirmDialog.Show("File doesn't exist yet",
                    $"{_currentFilePath}\n\nIt will be created on first Save.",
                    "OK", this);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _currentFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open {_currentFilePath}: {ex.Message}");
        }
    }

    private void BtnInsertTemplate_Click(object sender, RoutedEventArgs e)
    {
        const string template = """

            # CLAUDE.md

            ## Build Commands
            ```bash
            # Build
            dotnet build

            # Test
            dotnet test

            # Run
            dotnet run
            ```

            ## Architecture
            - Briefly describe the project structure, key directories, and dependencies.

            ## Coding Conventions
            - Language/framework conventions, naming rules, formatting preferences.

            ## Testing
            - Test framework, how to run tests, patterns to follow.

            ## Common Patterns
            - Recurring patterns, utilities, or idioms used in this codebase.

            """;

        // Append (don't overwrite) so existing content is preserved
        var current = txtContent.Text ?? "";
        if (!string.IsNullOrWhiteSpace(current) && !current.EndsWith('\n'))
        {
            txtContent.Text = current + "\n" + template.TrimStart();
        }
        else
        {
            txtContent.Text = current + template.TrimStart();
        }
        txtContent.ScrollToEnd();
    }

    private void ChkWordWrap_Changed(object sender, RoutedEventArgs e)
    {
        txtContent.TextWrapping = chkWordWrap.IsChecked == true
            ? TextWrapping.Wrap
            : TextWrapping.NoWrap;
        txtContent.HorizontalScrollBarVisibility = chkWordWrap.IsChecked == true
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    // ---- Auto-memory tab ----

    private void BuildMemoryProjectList()
    {
        var projects = ClaudeConfigService.ListProjectsWithMemory();
        cmbMemProject.ItemsSource = projects;

        // Build type filter dropdown
        cmbMemType.Items.Clear();
        cmbMemType.Items.Add("All types");
        cmbMemType.Items.Add("user");
        cmbMemType.Items.Add("feedback");
        cmbMemType.Items.Add("project");
        cmbMemType.Items.Add("reference");
        cmbMemType.SelectedIndex = 0;
    }

    private void CmbMemProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmbMemProject.SelectedItem is not MemoryProjectInfo project)
        {
            return;
        }

        txtMemDirPath.Text = project.MemoryDirectory;
        _allMemEntries = ClaudeConfigService.LoadMemoryEntries(project.MemoryDirectory);
        ApplyMemoryFilter();
        ClearMemoryPreview();
    }

    private void ApplyMemoryFilter()
    {
        var query = (txtMemSearch?.Text ?? "").Trim();
        var typeFilter = cmbMemType?.SelectedItem as string ?? "All types";
        IEnumerable<MemoryEntry> filtered = _allMemEntries;

        if (typeFilter != "All types" && !string.IsNullOrEmpty(typeFilter))
        {
            filtered = filtered.Where(e =>
                string.Equals(e.Type, typeFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Body.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        lstMemEntries.ItemsSource = filtered.ToList();
    }

    private void TxtMemSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyMemoryFilter();
    }

    private void CmbMemType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyMemoryFilter();
    }

    private void LstMemEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMemSelection)
        {
            return;
        }

        var selected = lstMemEntries.SelectedItems.Cast<MemoryEntry>().ToList();
        btnDeleteMem.IsEnabled = selected.Count > 0;

        if (selected.Count == 1)
        {
            var entry = selected[0];
            txtMemName.Text = entry.Name;
            txtMemMeta.Text = $"Type: {entry.Type}    File: {entry.FileName}    Modified: {entry.LastModifiedDisplay}";
            txtMemDescription.Text = entry.Description;
            txtMemBody.Text = entry.Body;
        }
        else if (selected.Count > 1)
        {
            txtMemName.Text = $"{selected.Count} entries selected";
            txtMemMeta.Text = string.Join(", ", selected.Select(e => e.FileName));
            txtMemDescription.Text = "Click \"Delete selected\" to remove all selected entries.";
            txtMemBody.Text = "";
        }
        else
        {
            ClearMemoryPreview();
        }
    }

    private void ClearMemoryPreview()
    {
        txtMemName.Text = "Select an entry or click \"View MEMORY.md\"";
        txtMemMeta.Text = "";
        txtMemDescription.Text = "Click an entry on the left to see its contents.\n\n" +
                                 "\"View MEMORY.md\" shows the index file that Claude reads at the start\n" +
                                 "of every conversation to know what it has remembered about this project.";
        txtMemBody.Text = "";
        btnDeleteMem.IsEnabled = false;
    }

    private void BtnViewMemoryIndex_Click(object sender, RoutedEventArgs e)
    {
        if (cmbMemProject.SelectedItem is not MemoryProjectInfo project)
        {
            return;
        }

        var indexPath = System.IO.Path.Combine(project.MemoryDirectory, "MEMORY.md");
        var content = ClaudeConfigService.LoadText(indexPath);

        _suppressMemSelection = true;
        lstMemEntries.UnselectAll();
        _suppressMemSelection = false;

        txtMemName.Text = "MEMORY.md  (index)";
        txtMemMeta.Text = $"File: {indexPath}";
        txtMemDescription.Text = "This is the table of contents that Claude loads at the start of every conversation.\n" +
                                 "Each line points to an individual entry file. Claude reads the full entry only when relevant.";
        txtMemBody.Text = string.IsNullOrWhiteSpace(content) ? "(empty — no entries yet)" : content;
        btnDeleteMem.IsEnabled = false;
    }

    private void BtnDeleteMem_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedMemoryEntries();
    }

    private void MenuDeleteMem_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedMemoryEntries();
    }

    private void DeleteSelectedMemoryEntries()
    {
        var selected = lstMemEntries.SelectedItems.Cast<MemoryEntry>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var message = selected.Count == 1
            ? $"Delete this memory entry?\n\n  {selected[0].Name}\n  ({selected[0].FileName})"
            : $"Delete {selected.Count} memory entries?\n\n" +
              string.Join("\n", selected.Take(10).Select(e => $"  {e.Name}  ({e.FileName})")) +
              (selected.Count > 10 ? $"\n  ... and {selected.Count - 10} more" : "");

        var ok = ConfirmDialog.Show(
            selected.Count == 1 ? "Delete memory entry?" : $"Delete {selected.Count} entries?",
            $"{message}\n\nFiles will be removed from disk and their lines stripped from MEMORY.md.\nThis cannot be undone.",
            "Delete", this);
        if (!ok)
        {
            return;
        }

        int deleted = 0;
        foreach (var entry in selected)
        {
            try
            {
                ClaudeConfigService.DeleteMemoryEntry(entry);
                _allMemEntries.Remove(entry);
                deleted++;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to delete memory entry {entry.FileName}", ex);
            }
        }

        ApplyMemoryFilter();
        ClearMemoryPreview();

        if (deleted < selected.Count)
        {
            ConfirmDialog.Show("Partial delete",
                $"Deleted {deleted} of {selected.Count} entries.\nSee app.log for details on failures.",
                "OK", this);
        }
    }

    private void BtnRefreshMem_Click(object sender, RoutedEventArgs e)
    {
        BuildMemoryProjectList();
        if (cmbMemProject.SelectedItem is MemoryProjectInfo project)
        {
            _allMemEntries = ClaudeConfigService.LoadMemoryEntries(project.MemoryDirectory);
            ApplyMemoryFilter();
        }
    }

    private void MenuOpenMemFile_Click(object sender, RoutedEventArgs e)
    {
        if (lstMemEntries.SelectedItem is not MemoryEntry entry)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open {entry.FilePath}: {ex.Message}");
        }
    }

    private void TxtMemDirPath_Click(object sender, MouseButtonEventArgs e)
    {
        var path = txtMemDirPath.Text;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open memory dir {path}: {ex.Message}");
        }
    }

    private void ChkContentPreview_Changed(object sender, RoutedEventArgs e)
    {
        var preview = chkContentPreview.IsChecked == true;
        txtContent.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        mdContentPreview.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
        {
            mdContentPreview.Markdown = txtContent.Text;
            mdContentPreview.SetValue(FlowDocumentScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        }
    }

    private void ChkMemPreview_Changed(object sender, RoutedEventArgs e)
    {
        var preview = chkMemPreview.IsChecked == true;
        txtMemBody.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        mdMemBody.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
        {
            mdMemBody.Markdown = txtMemBody.Text;
            mdMemBody.SetValue(FlowDocumentScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        }
    }

    // ---- Window lifecycle ----

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // Read-only TextBoxes eat wheel events — bubble to parent ScrollViewer.
        // MarkdownViewer handles its own scrolling (has internal FlowDocumentScrollViewer).
        if (!e.Handled && e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase { IsReadOnly: true } tb)
        {
            e.Handled = true;
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(tb) as DependencyObject;
            while (parent != null && parent is not ScrollViewer)
            {
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            if (parent is ScrollViewer sv)
            {
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
            }
        }
        base.OnPreviewMouseWheel(e);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_isDirty)
        {
            return;
        }

        var ok = ConfirmDialog.Show("Discard unsaved changes?",
            "You have unsaved CLAUDE.md changes. Close anyway?",
            "Discard", this);
        if (!ok)
        {
            e.Cancel = true;
        }
    }

    private class ScopeOption
    {
        public string DisplayName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public override string ToString() => DisplayName;
    }
}
