using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class PromptLibraryWindow : Window
{
    private const string AllCategories = "(All)";
    private List<PromptEntry> _allPrompts = [];
    private PromptEntry? _currentEntry;
    private readonly List<QuickLaunchEntry> _projects;
    private readonly string? _selectPromptId;

    public PromptLibraryWindow(List<QuickLaunchEntry> projects, string? selectPromptId = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _projects = projects;
        _selectPromptId = selectPromptId;

        LoadPrompts();
        PopulateProjectCombo();

        if (_selectPromptId != null)
        {
            SelectPromptById(_selectPromptId);
        }
        else
        {
            SetEditEnabled(false);
        }
    }

    private void SelectPromptById(string id)
    {
        var items = lstPrompts.ItemsSource as List<PromptEntry>;
        var item = items?.FirstOrDefault(p => p.Id == id);
        if (item != null)
        {
            lstPrompts.SelectedItem = item;
            lstPrompts.ScrollIntoView(item);
        }
    }

    private void LoadPrompts()
    {
        PromptService.InvalidateCache();
        _allPrompts = PromptService.Load();
        RebuildCategoryFilter();
        ApplyFilter();
    }

    private void RebuildCategoryFilter()
    {
        var current = (cmbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString();

        cmbCategory.Items.Clear();
        cmbCategory.Items.Add(new ComboBoxItem { Content = AllCategories });

        foreach (var cat in PromptService.GetCategories())
        {
            cmbCategory.Items.Add(new ComboBoxItem { Content = cat });
        }

        // Restore selection
        var found = false;
        if (current != null)
        {
            for (int i = 0; i < cmbCategory.Items.Count; i++)
            {
                if (((ComboBoxItem)cmbCategory.Items[i]).Content?.ToString() == current)
                {
                    cmbCategory.SelectedIndex = i;
                    found = true;
                    break;
                }
            }
        }
        if (!found)
        {
            cmbCategory.SelectedIndex = 0;
        }

        // Also update edit category combo
        RebuildEditCategoryCombo();
    }

    private void RebuildEditCategoryCombo()
    {
        // Category is now a plain TextBox - no rebuild needed
    }

    private void ApplyFilter()
    {
        var category = (cmbCategory.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? AllCategories;
        var search = txtSearch?.Text?.Trim() ?? "";

        var filtered = category == AllCategories
            ? _allPrompts.AsEnumerable()
            : _allPrompts.Where(p => p.Category == category);

        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(p =>
                p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.Prompt.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        lstPrompts.ItemsSource = filtered.OrderBy(p => p.Category).ThenBy(p => p.Name).ToList();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilter();
        }
    }

    private void PopulateProjectCombo()
    {
        cmbProject.Items.Clear();
        foreach (var project in _projects)
        {
            cmbProject.Items.Add(new ComboBoxItem { Content = project.Name, Tag = project });
        }
        if (cmbProject.Items.Count > 0)
        {
            cmbProject.SelectedIndex = 0;
        }
    }

    private void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilter();
        }
    }

    private void LstPrompts_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentEntry = lstPrompts.SelectedItem as PromptEntry;
        var hasSelection = _currentEntry != null;

        btnDelete.IsEnabled = hasSelection;
        btnSave.IsEnabled = hasSelection;
        SetEditEnabled(hasSelection);

        if (_currentEntry != null)
        {
            txtName.Text = _currentEntry.Name;
            cmbEditCategory.Text = _currentEntry.Category;
            txtPrompt.Text = _currentEntry.Prompt;
        }
        else
        {
            ClearForm();
        }

        UpdateRunButton();
    }

    private void SetEditEnabled(bool enabled)
    {
        txtName.IsEnabled = enabled;
        cmbEditCategory.IsEnabled = enabled;
        txtPrompt.IsEnabled = enabled;
        btnCopy.IsEnabled = enabled;
        cmbProject.IsEnabled = enabled;
    }

    private void UpdateRunButton()
    {
        btnRun.IsEnabled = !string.IsNullOrWhiteSpace(txtPrompt.Text)
            && cmbProject.SelectedItem != null;
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var entry = new PromptEntry
        {
            Name = "New prompt",
            Category = "General",
            Prompt = ""
        };

        PromptService.Add(entry);
        LoadPrompts();

        // Select the new entry
        var items = lstPrompts.ItemsSource as List<PromptEntry>;
        var newItem = items?.FirstOrDefault(p => p.Id == entry.Id);
        if (newItem != null)
        {
            lstPrompts.SelectedItem = newItem;
        }

        txtName.Focus();
        txtName.SelectAll();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry == null)
        {
            return;
        }

        if (!ConfirmDialog.Show("Delete Prompt",
            $"Delete prompt \"{_currentEntry.Name}\"?", "Delete", this))
        {
            return;
        }

        PromptService.Remove(_currentEntry.Id);
        _currentEntry = null;
        ClearForm();
        LoadPrompts();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(txtName.Text))
        {
            return;
        }

        _currentEntry.Name = txtName.Text.Trim();
        _currentEntry.Category = string.IsNullOrWhiteSpace(cmbEditCategory.Text)
            ? "General"
            : cmbEditCategory.Text.Trim();
        _currentEntry.Prompt = txtPrompt.Text;

        PromptService.Update(_currentEntry);

        var savedId = _currentEntry.Id;
        LoadPrompts();

        // Re-select the saved entry
        var items = lstPrompts.ItemsSource as List<PromptEntry>;
        var item = items?.FirstOrDefault(p => p.Id == savedId);
        if (item != null)
        {
            lstPrompts.SelectedItem = item;
        }
    }

    private void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(txtPrompt.Text) || cmbProject.SelectedItem is not ComboBoxItem ci)
        {
            return;
        }

        var project = ci.Tag as QuickLaunchEntry;
        if (project == null)
        {
            return;
        }

        ClaudeLauncherService.LaunchWithPrompt(project.FolderPath, txtPrompt.Text.Trim());
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(txtPrompt.Text))
        {
            Clipboard.SetText(txtPrompt.Text);
        }
    }

    private void ClearForm()
    {
        txtName.Text = "";
        cmbEditCategory.Text = "";
        txtPrompt.Text = "";
        btnSave.IsEnabled = false;
        btnDelete.IsEnabled = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
