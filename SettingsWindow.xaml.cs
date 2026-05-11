using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<QuickLaunchEntry> _entries = [];
    private readonly AppSettings _settings;
    private bool _suppressModEvents;
    private string _initialState = "";

    public bool SettingsChanged { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);

        foreach (var entry in settings.QuickLaunchEntries)
        {
            _entries.Add(new QuickLaunchEntry
            {
                Name = entry.Name,
                FolderPath = entry.FolderPath,
                Hotkey = entry.Hotkey,
                ContinueLastSession = entry.ContinueLastSession,
                ShellOverride = entry.ShellOverride,
                ModelOverride = entry.ModelOverride
            });
        }

        lstEntries.ItemsSource = _entries;
        PopulateEntryShellCombo();
        PopulateEntryModelCombo();
        PopulateCombos();
        LoadGlobalHotkeys();
        LoadCompactSettings();
        LoadGeneralSettings();

        bool shortcutsDisabled;
        using (var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
        {
            var val = regKey?.GetValue("ClaudeSessionsSidekick_Hotkey");
            shortcutsDisabled = val is not int intVal || intVal != 1;
            if (shortcutsDisabled)
            {
                txtShortcutsWarning.Visibility = Visibility.Visible;
                txtQuickLaunchWarning.Visibility = Visibility.Visible;
            }
        }

        Loaded += (_, _) => _initialState = GetCurrentStateSnapshot();
    }

    private string GetCurrentStateSnapshot()
    {
        var parts = new List<string>();
        foreach (var e in _entries)
        {
            // If this entry is selected, use form values instead (user may have edited without clicking Update)
            if (lstEntries.SelectedItem == e)
            {
                parts.Add($"{txtName.Text.Trim()}|{txtFolder.Text.Trim()}|{BuildHotkeyString()}|{chkContinue.IsChecked == true}|{GetSelectedEntryShell()}|{GetSelectedEntryModel()}");
            }
            else
            {
                parts.Add($"{e.Name}|{e.FolderPath}|{e.Hotkey}|{e.ContinueLastSession}|{e.ShellOverride}|{e.ModelOverride}");
            }
        }
        // Also check if form has new entry data (nothing selected but form filled)
        if (lstEntries.SelectedItem == null && !string.IsNullOrWhiteSpace(txtName.Text))
        {
            parts.Add($"NEW|{txtName.Text.Trim()}|{txtFolder.Text.Trim()}");
        }
        parts.Add(BuildHotkeyFromCombos(cmbWidgetMod1, cmbWidgetMod2, cmbWidgetKey) ?? "");
        parts.Add(BuildHotkeyFromCombos(cmbBrowserMod1, cmbBrowserMod2, cmbBrowserKey) ?? "");
        parts.Add(BuildHotkeyFromCombos(cmbPromptMod1, cmbPromptMod2, cmbPromptKey) ?? "");
        parts.Add(BuildHotkeyFromCombos(cmbPermMod1, cmbPermMod2, cmbPermKey) ?? "");
        parts.Add(BuildHotkeyFromCombos(cmbClaudeMod1, cmbClaudeMod2, cmbClaudeKey) ?? "");
        parts.Add(BuildHotkeyFromCombos(cmbAgentsMod1, cmbAgentsMod2, cmbAgentsKey) ?? "");
        parts.Add(((cmbAggressiveness?.SelectedItem as ComboBoxItem)?.Tag ?? CompactAggressiveness.Balanced).ToString()!);
        parts.Add((chkNotifications?.IsChecked == true).ToString());
        parts.Add((chkPermissionSuggestions?.IsChecked == true).ToString());
        parts.Add((chkShowActiveSessions?.IsChecked == true).ToString());
        parts.Add((chkCheckForUpdatesOnStartup?.IsChecked == true).ToString());
        parts.Add(((cmbShell?.SelectedItem as ComboBoxItem)?.Tag ?? ShellType.Auto).ToString()!);
        parts.Add(txtClaudeExe?.Text?.Trim() ?? "");
        parts.Add(txtClaudeHome?.Text?.Trim() ?? "");
        return string.Join(";;", parts);
    }

    private bool HasUnsavedChanges() => _initialState != GetCurrentStateSnapshot();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            TryClose();
        }
    }

    private bool _forceClose;

    private void TryClose()
    {
        if (HasUnsavedChanges())
        {
            if (!ConfirmDialog.Show("Unsaved Changes",
                "You have unsaved changes. Discard and close?", "Discard", this))
            {
                return;
            }
        }

        _forceClose = true;
        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!SettingsChanged && !_forceClose && HasUnsavedChanges())
        {
            e.Cancel = true;
            // Defer to avoid re-entrancy
            Dispatcher.BeginInvoke(() => TryClose());
            return;
        }
        base.OnClosing(e);
    }

    private void PopulateEntryShellCombo()
    {
        cmbEntryShell.Items.Clear();
        cmbEntryShell.Items.Add(new ComboBoxItem { Content = "Use global" });
        cmbEntryShell.Items.Add(new ComboBoxItem { Content = "CMD", Tag = ShellType.Cmd });
        cmbEntryShell.Items.Add(new ComboBoxItem { Content = "PowerShell", Tag = ShellType.PowerShell });
        cmbEntryShell.Items.Add(new ComboBoxItem { Content = "Git Bash", Tag = ShellType.GitBash });
        cmbEntryShell.SelectedIndex = 0;
    }

    private void SelectEntryShell(ShellType? shell)
    {
        if (shell == null)
        {
            cmbEntryShell.SelectedIndex = 0;
            return;
        }
        for (int i = 1; i < cmbEntryShell.Items.Count; i++)
        {
            if (((ComboBoxItem)cmbEntryShell.Items[i]).Tag is ShellType tag && tag == shell.Value)
            {
                cmbEntryShell.SelectedIndex = i;
                return;
            }
        }
        cmbEntryShell.SelectedIndex = 0;
    }

    private ShellType? GetSelectedEntryShell()
    {
        return (cmbEntryShell.SelectedItem as ComboBoxItem)?.Tag is ShellType s ? s : null;
    }

    private void PopulateEntryModelCombo()
    {
        cmbEntryModel.Items.Clear();
        cmbEntryModel.Items.Add(new ComboBoxItem { Content = "Default (Claude decides)" });
        cmbEntryModel.Items.Add(new ComboBoxItem { Content = "Sonnet (1M)",  Tag = "sonnet" });
        cmbEntryModel.Items.Add(new ComboBoxItem { Content = "Opus (1M)",    Tag = "opus" });
        cmbEntryModel.Items.Add(new ComboBoxItem { Content = "Haiku (200k)", Tag = "haiku" });
        cmbEntryModel.SelectedIndex = 0;
    }

    private void SelectEntryModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            cmbEntryModel.SelectedIndex = 0;
            return;
        }
        for (int i = 1; i < cmbEntryModel.Items.Count; i++)
        {
            if (((ComboBoxItem)cmbEntryModel.Items[i]).Tag is string tag && tag == model)
            {
                cmbEntryModel.SelectedIndex = i;
                return;
            }
        }
        // Unknown stored override (e.g. user hand-edited settings.json with a
        // pinned version like claude-opus-4-7) — fall back to default; on save
        // we'll write whatever they reselect, dropping the unknown value.
        cmbEntryModel.SelectedIndex = 0;
    }

    private string? GetSelectedEntryModel()
    {
        return (cmbEntryModel.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private void PopulateCombos()
    {
        _suppressModEvents = true;

        cmbMod1.Items.Clear();
        foreach (var (display, _) in HotkeyHelper.Modifiers)
        {
            cmbMod1.Items.Add(new ComboBoxItem { Content = display });
        }
        cmbMod1.SelectedIndex = 6; // Win

        RebuildMod2Combo();

        cmbKey.Items.Clear();
        cmbKey.Items.Add(new ComboBoxItem { Content = "(none)" });
        foreach (var key in HotkeyHelper.KeyNames)
        {
            cmbKey.Items.Add(new ComboBoxItem { Content = key });
        }
        cmbKey.SelectedIndex = 0;

        _suppressModEvents = false;
    }

    private void RebuildMod2Combo()
    {
        var currentMod2 = GetComboValue(cmbMod2);
        var selectedMod1 = GetComboValue(cmbMod1);

        _suppressModEvents = true;
        cmbMod2.Items.Clear();
        cmbMod2.Items.Add(new ComboBoxItem { Content = "(none)" });

        foreach (var (display, _) in HotkeyHelper.Modifiers)
        {
            if (display != selectedMod1)
            {
                cmbMod2.Items.Add(new ComboBoxItem { Content = display });
            }
        }

        // Restore previous selection if still available
        SelectComboValue(cmbMod2, currentMod2);
        _suppressModEvents = false;
    }

    private void CmbMod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModEvents)
        {
            return;
        }

        if (sender == cmbMod1)
        {
            RebuildMod2Combo();
        }

        CheckHotkeyAvailability();
    }

    private void CheckHotkeyAvailability()
    {
        txtHotkeyWarning.Visibility = Visibility.Collapsed;

        var hotkey = BuildHotkeyString();
        if (hotkey == null)
        {
            return;
        }

        // Check conflict with other entries
        var conflict = _entries.FirstOrDefault(e =>
            e.Hotkey == hotkey && e != lstEntries.SelectedItem);
        if (conflict != null)
        {
            txtHotkeyWarning.Text = $"Already assigned to \"{conflict.Name}\"";
            txtHotkeyWarning.Visibility = Visibility.Visible;
            return;
        }

        // With low-level keyboard hook, there's no system-level conflict.
        // Just validate that the hotkey string parses correctly.
        if (!HotkeyHelper.TryParse(hotkey, out _, out _))
        {
            txtHotkeyWarning.Text = $"Invalid hotkey: {hotkey}";
            txtHotkeyWarning.Visibility = Visibility.Visible;
        }
    }

    private string? BuildHotkeyString()
    {
        var key = GetComboValue(cmbKey);
        if (key == null || key == "(none)")
        {
            return null;
        }

        var mod1Display = GetComboValue(cmbMod1);
        var mod2Display = GetComboValue(cmbMod2);
        if (mod1Display == null)
        {
            return null;
        }

        var mod1 = HotkeyHelper.DisplayToValue(mod1Display);
        var mod2 = mod2Display is null or "(none)" ? null : HotkeyHelper.DisplayToValue(mod2Display);

        return HotkeyHelper.Build(mod1, mod2, key);
    }

    private void LstEntries_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Deselect if clicking on already selected item
        if (e.OriginalSource is FrameworkElement fe)
        {
            var item = fe.DataContext as QuickLaunchEntry;
            if (item != null && lstEntries.SelectedItem == item)
            {
                lstEntries.SelectedItem = null;
                ClearInputs();
                e.Handled = true;
            }
        }
    }

    private void LstEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = lstEntries.SelectedItem as QuickLaunchEntry;
        bool hasSelection = selected != null;

        btnAdd.IsEnabled = !hasSelection;
        btnUpdate.IsEnabled = hasSelection;
        btnRemove.IsEnabled = hasSelection;

        if (selected != null)
        {
            txtName.Text = selected.Name;
            txtFolder.Text = selected.FolderPath;
            chkContinue.IsChecked = selected.ContinueLastSession;
            SelectEntryShell(selected.ShellOverride);
            SelectEntryModel(selected.ModelOverride);
            LoadHotkeyIntoCombos(selected.Hotkey);
        }
    }

    private void LoadHotkeyIntoCombos(string? hotkey)
    {
        _suppressModEvents = true;
        txtHotkeyWarning.Visibility = Visibility.Collapsed;

        if (string.IsNullOrEmpty(hotkey))
        {
            cmbMod1.SelectedIndex = 6; // Win
            RebuildMod2Combo();
            cmbKey.SelectedIndex = 0; // (none)
            _suppressModEvents = false;
            return;
        }

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            _suppressModEvents = false;
            return;
        }

        if (parts.Length == 2)
        {
            // Single modifier + key — convert saved value to display name
            SelectComboValue(cmbMod1, HotkeyHelper.ValueToDisplay(parts[0]));
            RebuildMod2Combo();
            SelectComboValue(cmbMod2, "(none)");
            SelectComboValue(cmbKey, parts[1]);
        }
        else
        {
            // Two modifiers + key
            SelectComboValue(cmbMod1, HotkeyHelper.ValueToDisplay(parts[0]));
            RebuildMod2Combo();
            SelectComboValue(cmbMod2, HotkeyHelper.ValueToDisplay(parts[1]));
            SelectComboValue(cmbKey, parts[2]);
        }

        _suppressModEvents = false;
    }

    private static string? GetComboValue(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Content?.ToString();
    }

    private static void SelectComboValue(ComboBox combo, string? value)
    {
        if (value == null)
        {
            combo.SelectedIndex = 0;
            return;
        }

        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (((ComboBoxItem)combo.Items[i]).Content?.ToString() == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(txtName.Text))
        {
            ConfirmDialog.Show("Validation", "Please enter a name.", "OK", this);
            return false;
        }

        if (string.IsNullOrWhiteSpace(txtFolder.Text) || !Directory.Exists(txtFolder.Text))
        {
            ConfirmDialog.Show("Validation", "Please select a valid folder path.\nThe folder does not exist.", "OK", this);
            return false;
        }

        var hotkey = BuildHotkeyString();
        if (hotkey != null)
        {
            var conflict = _entries.FirstOrDefault(e =>
                e.Hotkey == hotkey && e != lstEntries.SelectedItem);
            if (conflict != null)
            {
                ConfirmDialog.Show("Conflict", $"Hotkey {hotkey} is already assigned to Project \"{conflict.Name}\".", "OK", this);
                return false;
            }
        }

        return true;
    }

    private void TxtFolder_TextChanged(object sender, TextChangedEventArgs e)
    {
        var path = txtFolder.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            txtFolderWarning.Visibility = Visibility.Collapsed;
        }
        else if (!Directory.Exists(path))
        {
            txtFolderWarning.Text = "Folder does not exist";
            txtFolderWarning.Visibility = Visibility.Visible;
        }
        else
        {
            txtFolderWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select project folder",
            UseDescriptionForTitle = true
        };

        if (!string.IsNullOrEmpty(txtFolder.Text) && Directory.Exists(txtFolder.Text))
        {
            dialog.InitialDirectory = txtFolder.Text;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            txtFolder.Text = dialog.SelectedPath;
        }
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInput())
        {
            return;
        }

        var entry = new QuickLaunchEntry
        {
            Name = txtName.Text.Trim(),
            FolderPath = ClaudeLauncherService.NormalizeWorkingDir(txtFolder.Text.Trim()),
            Hotkey = BuildHotkeyString(),
            ContinueLastSession = chkContinue.IsChecked == true,
            ShellOverride = GetSelectedEntryShell(),
            ModelOverride = GetSelectedEntryModel()
        };

        _entries.Add(entry);
        lstEntries.SelectedItem = null;
        ClearInputs();
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (lstEntries.SelectedItem is not QuickLaunchEntry selected)
        {
            return;
        }

        if (!ValidateInput())
        {
            return;
        }

        selected.Name = txtName.Text.Trim();
        selected.FolderPath = ClaudeLauncherService.NormalizeWorkingDir(txtFolder.Text.Trim());
        selected.Hotkey = BuildHotkeyString();
        selected.ContinueLastSession = chkContinue.IsChecked == true;
        selected.ShellOverride = GetSelectedEntryShell();
        selected.ModelOverride = GetSelectedEntryModel();

        // Refresh the list display
        var idx = lstEntries.SelectedIndex;
        lstEntries.ItemsSource = null;
        lstEntries.ItemsSource = _entries;
        lstEntries.SelectedIndex = idx;

        // Brief click feedback so the user can see something happened — without
        // it the only visible change is in the form (which is already filled),
        // so editing only Model/Shell looks like a no-op.
        await FlashUpdatedAsync();
    }

    private async System.Threading.Tasks.Task FlashUpdatedAsync()
    {
        // Bump a token so concurrent clicks don't restore the text mid-flash.
        // Whichever click was last wins; earlier completions exit early.
        var token = ++_updateFlashToken;
        btnUpdate.Content = "✓ Updated";
        try
        {
            await System.Threading.Tasks.Task.Delay(800);
        }
        catch (TaskCanceledException)
        {
            // Window closed mid-flash — ignore.
        }
        if (token == _updateFlashToken)
        {
            btnUpdate.Content = "Update";
        }
    }

    private int _updateFlashToken;

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        if (lstEntries.SelectedItem is not QuickLaunchEntry selected)
        {
            return;
        }

        if (!ConfirmDialog.Show("Remove Entry", $"Remove \"{selected.Name}\" from Quick Launch?", "Remove", this))
        {
            return;
        }

        _entries.Remove(selected);
        ClearInputs();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SettingsChanged = true;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        TryClose();
    }

    public List<QuickLaunchEntry> GetEntries() => [.. _entries];

    public AppSettings GetUpdatedSettings()
    {
        _settings.QuickLaunchEntries = GetEntries();
        _settings.WidgetToggleHotkey = BuildHotkeyFromCombos(cmbWidgetMod1, cmbWidgetMod2, cmbWidgetKey)
            ?? _settings.WidgetToggleHotkey;
        _settings.SessionBrowserHotkey = BuildHotkeyFromCombos(cmbBrowserMod1, cmbBrowserMod2, cmbBrowserKey)
            ?? _settings.SessionBrowserHotkey;
        _settings.PromptLibraryHotkey = BuildHotkeyFromCombos(cmbPromptMod1, cmbPromptMod2, cmbPromptKey)
            ?? _settings.PromptLibraryHotkey;
        _settings.PermissionManagerHotkey = BuildHotkeyFromCombos(cmbPermMod1, cmbPermMod2, cmbPermKey)
            ?? _settings.PermissionManagerHotkey;
        _settings.ClaudeConfigHotkey = BuildHotkeyFromCombos(cmbClaudeMod1, cmbClaudeMod2, cmbClaudeKey)
            ?? _settings.ClaudeConfigHotkey;
        _settings.AgentsSkillsHotkey = BuildHotkeyFromCombos(cmbAgentsMod1, cmbAgentsMod2, cmbAgentsKey)
            ?? _settings.AgentsSkillsHotkey;
        _settings.CompactAggressiveness = (cmbAggressiveness.SelectedItem as ComboBoxItem)?.Tag is CompactAggressiveness a
            ? a : CompactAggressiveness.Balanced;
        _settings.CustomWarningPercent = (int)sliderWarning.Value;
        _settings.CustomCriticalPercent = (int)sliderCritical.Value;
        _settings.EnableCompactNotifications = chkNotifications.IsChecked == true;
        _settings.EnablePermissionSuggestions = chkPermissionSuggestions.IsChecked == true;
        _settings.ShowActiveSessions = chkShowActiveSessions.IsChecked == true;
        _settings.CheckForUpdatesOnStartup = chkCheckForUpdatesOnStartup.IsChecked == true;
        _settings.PreferredShell = (cmbShell.SelectedItem as ComboBoxItem)?.Tag is ShellType s
            ? s : ShellType.Auto;
        var exePath = txtClaudeExe.Text.Trim();
        _settings.ClaudeExePath = string.IsNullOrEmpty(exePath) ? null : exePath;
        var homeDir = txtClaudeHome.Text.Trim();
        _settings.ClaudeHomeDir = string.IsNullOrEmpty(homeDir) ? null : homeDir;
        return _settings;
    }

    private void LoadGlobalHotkeys()
    {
        PopulateGlobalComboSet(cmbWidgetMod1, cmbWidgetMod2, cmbWidgetKey, _settings.WidgetToggleHotkey);
        PopulateGlobalComboSet(cmbBrowserMod1, cmbBrowserMod2, cmbBrowserKey, _settings.SessionBrowserHotkey);
        PopulateGlobalComboSet(cmbPromptMod1, cmbPromptMod2, cmbPromptKey, _settings.PromptLibraryHotkey);
        PopulateGlobalComboSet(cmbPermMod1, cmbPermMod2, cmbPermKey, _settings.PermissionManagerHotkey);
        PopulateGlobalComboSet(cmbClaudeMod1, cmbClaudeMod2, cmbClaudeKey, _settings.ClaudeConfigHotkey);
        PopulateGlobalComboSet(cmbAgentsMod1, cmbAgentsMod2, cmbAgentsKey, _settings.AgentsSkillsHotkey);
    }

    private void LoadCompactSettings()
    {
        _suppressModEvents = true;

        cmbAggressiveness.Items.Clear();
        cmbAggressiveness.Items.Add(new ComboBoxItem { Content = "Conservative", Tag = CompactAggressiveness.Conservative });
        cmbAggressiveness.Items.Add(new ComboBoxItem { Content = "Balanced", Tag = CompactAggressiveness.Balanced });
        cmbAggressiveness.Items.Add(new ComboBoxItem { Content = "Aggressive", Tag = CompactAggressiveness.Aggressive });
        cmbAggressiveness.Items.Add(new ComboBoxItem { Content = "Custom", Tag = CompactAggressiveness.Custom });

        cmbAggressiveness.SelectedIndex = _settings.CompactAggressiveness switch
        {
            CompactAggressiveness.Conservative => 0,
            CompactAggressiveness.Aggressive => 2,
            CompactAggressiveness.Custom => 3,
            _ => 1
        };

        sliderWarning.Value = _settings.CustomWarningPercent;
        sliderCritical.Value = _settings.CustomCriticalPercent;
        UpdateSliderLabels();

        UpdateAggressivenessHint();
        cmbAggressiveness.SelectionChanged += (_, _) => UpdateAggressivenessHint();

        chkNotifications.IsChecked = _settings.EnableCompactNotifications;
        chkPermissionSuggestions.IsChecked = _settings.EnablePermissionSuggestions;
        chkShowActiveSessions.IsChecked = _settings.ShowActiveSessions;
        chkCheckForUpdatesOnStartup.IsChecked = _settings.CheckForUpdatesOnStartup;

        _suppressModEvents = false;
    }

    private void UpdateAggressivenessHint()
    {
        var tag = (cmbAggressiveness.SelectedItem as ComboBoxItem)?.Tag;
        txtAggressivenessHint.Text = tag switch
        {
            CompactAggressiveness.Conservative => "Warns at >80% context only",
            CompactAggressiveness.Aggressive => "Warns early, from 30%+",
            CompactAggressiveness.Custom => $"Warning {(int)sliderWarning.Value}% / Critical {(int)sliderCritical.Value}%",
            _ => "Default (50%/75% + dynamic)"
        };
        panelCustomThresholds.Visibility = tag is CompactAggressiveness.Custom
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SliderThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: fires during XAML init before controls are loaded
        if (sliderWarning == null || sliderCritical == null || txtWarningValue == null || txtCriticalValue == null)
        {
            return;
        }

        // Ensure warning < critical
        if (sliderWarning.Value >= sliderCritical.Value)
        {
            if (sender == sliderWarning)
            {
                sliderCritical.Value = Math.Min(95, sliderWarning.Value + 5);
            }
            else
            {
                sliderWarning.Value = Math.Max(10, sliderCritical.Value - 5);
            }
        }
        UpdateSliderLabels();
        if ((cmbAggressiveness.SelectedItem as ComboBoxItem)?.Tag is CompactAggressiveness.Custom)
        {
            txtAggressivenessHint.Text = $"Warning {(int)sliderWarning.Value}% / Critical {(int)sliderCritical.Value}%";
        }
    }

    private void UpdateSliderLabels()
    {
        txtWarningValue.Text = $"{(int)sliderWarning.Value}%";
        txtCriticalValue.Text = $"{(int)sliderCritical.Value}%";
    }

    private void PopulateGlobalComboSet(ComboBox mod1, ComboBox mod2, ComboBox key, string hotkey)
    {
        _suppressModEvents = true;

        mod1.Items.Clear();
        foreach (var (display, _) in HotkeyHelper.Modifiers)
        {
            mod1.Items.Add(new ComboBoxItem { Content = display });
        }

        mod2.Items.Clear();
        mod2.Items.Add(new ComboBoxItem { Content = "(none)" });
        foreach (var (display, _) in HotkeyHelper.Modifiers)
        {
            mod2.Items.Add(new ComboBoxItem { Content = display });
        }

        key.Items.Clear();
        foreach (var k in HotkeyHelper.KeyNames)
        {
            key.Items.Add(new ComboBoxItem { Content = k });
        }

        // Parse and set — convert saved values to display names
        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 3)
        {
            SelectComboValue(mod1, HotkeyHelper.ValueToDisplay(parts[0]));
            SelectComboValue(mod2, HotkeyHelper.ValueToDisplay(parts[1]));
            SelectComboValue(key, parts[2]);
        }
        else if (parts.Length == 2)
        {
            SelectComboValue(mod1, HotkeyHelper.ValueToDisplay(parts[0]));
            SelectComboValue(mod2, "(none)");
            SelectComboValue(key, parts[1]);
        }

        _suppressModEvents = false;
    }

    private static string? BuildHotkeyFromCombos(ComboBox mod1, ComboBox mod2, ComboBox key)
    {
        var m1Display = GetComboValue(mod1);
        var m2Display = GetComboValue(mod2);
        var k = GetComboValue(key);

        if (m1Display == null || k == null)
        {
            return null;
        }

        var m1 = HotkeyHelper.DisplayToValue(m1Display);
        var m2 = m2Display is null or "(none)" ? null : HotkeyHelper.DisplayToValue(m2Display);

        return HotkeyHelper.Build(m1, m2, k);
    }

    private void CmbGlobal_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModEvents)
        {
            return;
        }

        CheckGlobalHotkeyConflicts();
    }

    private void CheckGlobalHotkeyConflicts()
    {
        txtGlobalWarning.Visibility = Visibility.Collapsed;

        var widgetHk = BuildHotkeyFromCombos(cmbWidgetMod1, cmbWidgetMod2, cmbWidgetKey);
        var browserHk = BuildHotkeyFromCombos(cmbBrowserMod1, cmbBrowserMod2, cmbBrowserKey);
        var promptHk = BuildHotkeyFromCombos(cmbPromptMod1, cmbPromptMod2, cmbPromptKey);
        var permHk = BuildHotkeyFromCombos(cmbPermMod1, cmbPermMod2, cmbPermKey);
        var claudeHk = BuildHotkeyFromCombos(cmbClaudeMod1, cmbClaudeMod2, cmbClaudeKey);
        var agentsHk = BuildHotkeyFromCombos(cmbAgentsMod1, cmbAgentsMod2, cmbAgentsKey);

        // Check duplicates among global shortcuts
        var globalHotkeys = new[]
        {
            ("Toggle Widget", widgetHk),
            ("Session Browser", browserHk),
            ("Prompt Library", promptHk),
            ("Permission Manager", permHk),
            ("Memory Manager", claudeHk),
            ("Agents & Skills", agentsHk)
        };
        for (int i = 0; i < globalHotkeys.Length; i++)
        {
            for (int j = i + 1; j < globalHotkeys.Length; j++)
            {
                if (globalHotkeys[i].Item2 != null && globalHotkeys[i].Item2 == globalHotkeys[j].Item2)
                {
                    txtGlobalWarning.Text = $"{globalHotkeys[i].Item1} and {globalHotkeys[j].Item1} have the same shortcut";
                    txtGlobalWarning.Visibility = Visibility.Visible;
                    return;
                }
            }
        }

        // Check conflict with quick launch entries
        var warnings = new List<string>();
        foreach (var entry in _entries)
        {
            if (entry.Hotkey != null && (entry.Hotkey == widgetHk || entry.Hotkey == browserHk || entry.Hotkey == promptHk || entry.Hotkey == permHk || entry.Hotkey == claudeHk || entry.Hotkey == agentsHk))
            {
                warnings.Add($"{entry.Hotkey} conflicts with Quick Launch Project \"{entry.Name}\"");
            }
        }

        if (warnings.Count > 0)
        {
            txtGlobalWarning.Text = string.Join("\n", warnings);
            txtGlobalWarning.Visibility = Visibility.Visible;
        }
    }

    private void LoadGeneralSettings()
    {
        _suppressModEvents = true;

        cmbShell.Items.Clear();
        cmbShell.Items.Add(new ComboBoxItem { Content = "Auto-detect", Tag = ShellType.Auto });
        cmbShell.Items.Add(new ComboBoxItem { Content = "CMD", Tag = ShellType.Cmd });
        cmbShell.Items.Add(new ComboBoxItem { Content = "PowerShell", Tag = ShellType.PowerShell });
        cmbShell.Items.Add(new ComboBoxItem { Content = "Git Bash", Tag = ShellType.GitBash });

        cmbShell.SelectedIndex = _settings.PreferredShell switch
        {
            ShellType.Cmd => 1,
            ShellType.PowerShell => 2,
            ShellType.GitBash => 3,
            _ => 0
        };

        cmbShell.SelectionChanged += (_, _) =>
        {
            UpdateShellDetectionLabel();
            UpdateClaudePathVisibility();
        };

        txtClaudeExe.Text = _settings.ClaudeExePath ?? "";
        txtClaudeExe.TextChanged += (_, _) => UpdateClaudeResolvedLabel();

        txtClaudeHome.Text = _settings.ClaudeHomeDir ?? "";
        txtClaudeHome.TextChanged += (_, _) => { UpdateClaudeHomeStatus(); UpdateDetectedModel(); };
        UpdateClaudeHomeStatus();
        UpdateDetectedModel();

        UpdateShellDetectionLabel();
        UpdateClaudePathVisibility();

        _suppressModEvents = false;
    }

    private async void UpdateShellDetectionLabel()
    {
        var selected = (cmbShell.SelectedItem as ComboBoxItem)?.Tag;
        if (selected is ShellType.Auto)
        {
            txtShellDetected.Text = "(detecting...)";
            var detected = await Task.Run(() => ClaudeLauncherService.DetectShell());
            txtShellDetected.Text = $"(detected: {detected})";
        }
        else
        {
            txtShellDetected.Text = "";
        }
    }

    private void UpdateClaudePathVisibility()
    {
        var isAuto = (cmbShell.SelectedItem as ComboBoxItem)?.Tag is ShellType.Auto;
        var vis = isAuto ? Visibility.Collapsed : Visibility.Visible;
        lblClaudePath.Visibility = vis;
        txtClaudeExe.Visibility = vis;
        btnBrowseExe.Visibility = vis;
        txtClaudeResolved.Visibility = vis;
        txtClaudeNotFound.Visibility = Visibility.Collapsed;

        if (!isAuto)
        {
            UpdateClaudeResolvedLabel();
        }
    }

    private void UpdateClaudeResolvedLabel()
    {
        var customPath = txtClaudeExe.Text.Trim();

        if (!string.IsNullOrEmpty(customPath))
        {
            if (File.Exists(customPath))
            {
                txtClaudeResolved.Text = $"Using: {customPath}";
                txtClaudeNotFound.Visibility = Visibility.Collapsed;
            }
            else
            {
                txtClaudeResolved.Text = "";
                txtClaudeNotFound.Text = "Specified file does not exist";
                txtClaudeNotFound.Visibility = Visibility.Visible;
            }
            return;
        }

        var resolved = ClaudeLauncherService.ResolveClaudePath();
        if (resolved != null)
        {
            txtClaudeResolved.Text = $"Auto-detected: {resolved}";
            txtClaudeNotFound.Visibility = Visibility.Collapsed;
        }
        else
        {
            txtClaudeResolved.Text = "";
            txtClaudeNotFound.Text = "Claude CLI not found in PATH. Set the path above or install via: npm install -g @anthropic-ai/claude-code";
            txtClaudeNotFound.Visibility = Visibility.Visible;
        }
    }

    private void UpdateClaudeHomeStatus()
    {
        var custom = txtClaudeHome.Text.Trim();
        var detected = ClaudeConfigService.DetectedClaudeHomeDir;

        if (!string.IsNullOrEmpty(custom))
        {
            if (Directory.Exists(custom) && File.Exists(Path.Combine(custom, ".credentials.json")))
            {
                txtClaudeHomeStatus.Text = $"Valid config directory";
                txtClaudeHomeStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0xA0, 0x60));
            }
            else if (Directory.Exists(custom))
            {
                txtClaudeHomeStatus.Text = $"Directory exists but no .credentials.json found";
                txtClaudeHomeStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE8, 0xA0, 0x30));
            }
            else
            {
                txtClaudeHomeStatus.Text = "Directory does not exist";
                txtClaudeHomeStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE0, 0x50, 0x50));
            }
        }
        else
        {
            var exists = Directory.Exists(detected);
            txtClaudeHomeStatus.Text = $"Auto-detected: {detected}" + (exists ? "" : " (not found!)");
            txtClaudeHomeStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                exists
                    ? System.Windows.Media.Color.FromRgb(0x60, 0xA0, 0x60)
                    : System.Windows.Media.Color.FromRgb(0xE8, 0xA0, 0x30));
        }
    }

    private string? _modelSettingsFile;

    private void UpdateDetectedModel()
    {
        var configuredModel = ClaudeConfigService.GetConfiguredModel();
        var isShorthand = ClaudeConfigService.IsModelShorthand(configuredModel);

        // Determine which file has the model setting
        var localPath = Path.Combine(ClaudeConfigService.ClaudeHomeDir, "settings.local.json");
        var globalPath = Path.Combine(ClaudeConfigService.ClaudeHomeDir, "settings.json");
        _modelSettingsFile = null;

        // Always find the settings file for click-to-open
        _modelSettingsFile = File.Exists(globalPath) ? globalPath
            : File.Exists(localPath) ? localPath : null;

        if (configuredModel != null && isShorthand)
        {
            // Find which file contains the shorthand so user knows where to fix
            var sourceFile = ReadModelFrom(localPath) != null ? localPath
                : ReadModelFrom(globalPath) != null ? globalPath : null;

            txtDetectedModel.Text = $"\u26A0 \"{configuredModel}\" (200k context) \u2014 click to fix";
            txtDetectedModel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xE8, 0xA0, 0x30));
            txtDetectedModel.TextDecorations = TextDecorations.Underline;
            txtDetectedModel.ToolTip = sourceFile != null
                ? $"Open {Path.GetFileName(sourceFile)} to change model.\n\n" +
                  "Set \"model\": \"\" (empty string) to use the latest\n" +
                  "recommended model with 1M context window.\n\n" +
                  $"File: {sourceFile}"
                : "Set \"model\": \"\" in settings.local.json for 1M context.";

            _modelSettingsFile = sourceFile ?? _modelSettingsFile;
        }
        else if (configuredModel != null)
        {
            txtDetectedModel.Text = $"\"{configuredModel}\" \u2014 click to open settings";
            txtDetectedModel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x60, 0xA0, 0x60));
            txtDetectedModel.TextDecorations = TextDecorations.Underline;
            txtDetectedModel.ToolTip = $"Click to open settings.json\n\nFile: {_modelSettingsFile}";
        }
        else
        {
            txtDetectedModel.Text = $"Not set (latest recommended \u2014 1M context) \u2014 click to open";
            txtDetectedModel.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x60, 0xA0, 0x60));
            txtDetectedModel.TextDecorations = TextDecorations.Underline;
            txtDetectedModel.ToolTip = $"Click to open settings.json\n\nFile: {_modelSettingsFile}";
        }
    }

    private static string? ReadModelFrom(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void TxtDetectedModel_Click(object sender, MouseButtonEventArgs e)
    {
        if (_modelSettingsFile != null && File.Exists(_modelSettingsFile))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_modelSettingsFile) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to open settings file: {ex.Message}");
            }
        }
    }

    private void BtnBrowseHome_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select .claude configuration directory",
            UseDescriptionForTitle = true
        };

        var current = txtClaudeHome.Text.Trim();
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }
        else
        {
            dialog.InitialDirectory = ClaudeConfigService.DetectedClaudeHomeDir;
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            txtClaudeHome.Text = dialog.SelectedPath;
        }
    }

    private void BtnBrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select claude.exe",
            Filter = "Claude CLI|claude.exe;claude.cmd|All files|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrEmpty(txtClaudeExe.Text) && File.Exists(txtClaudeExe.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(txtClaudeExe.Text);
        }

        if (dialog.ShowDialog() == true)
        {
            txtClaudeExe.Text = dialog.FileName;
        }
    }

    private void ClearInputs()
    {
        txtName.Text = "";
        txtFolder.Text = "";
        _suppressModEvents = true;
        cmbMod1.SelectedIndex = 3; // Win
        RebuildMod2Combo();
        cmbKey.SelectedIndex = 0;
        _suppressModEvents = false;
        chkContinue.IsChecked = false;
        cmbEntryShell.SelectedIndex = 0;
        cmbEntryModel.SelectedIndex = 0;
        txtHotkeyWarning.Visibility = Visibility.Collapsed;
    }
}
