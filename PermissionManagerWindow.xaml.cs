using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class PermissionManagerWindow : Window
{
    private readonly List<QuickLaunchEntry> _projects;
    private readonly List<ScopeOption> _scopes = new();
    private PermissionFile? _currentFile;
    private List<RuleViewModel> _viewRules = new();
    private bool _isDirty;
    private bool _suppressDirty;

    public PermissionManagerWindow(List<QuickLaunchEntry> quickLaunchProjects)
    {
        InitializeComponent();
        Services.DarkTitleBar.Apply(this);

        _projects = quickLaunchProjects;
        BuildScopeList();
        BuildToolDropdown();
        BuildScopeDropdown();

        // Load global by default
        cmbScope.SelectedIndex = 0;
        Closing += OnWindowClosing;
    }

    // ---- Setup ----

    private void BuildScopeList()
    {
        _scopes.Add(new ScopeOption
        {
            DisplayName = "Global  (~/.claude/settings.json)",
            FilePath = PermissionService.GlobalSettingsPath,
            ShortName = "Global"
        });

        foreach (var p in _projects.Where(p => !string.IsNullOrWhiteSpace(p.FolderPath))
                                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Two scopes per project: shared (committed to repo) + local (gitignored)
            _scopes.Add(new ScopeOption
            {
                DisplayName = $"Project: {p.Name}  (shared)",
                FilePath = PermissionService.GetProjectSettingsPath(p.FolderPath),
                ShortName = $"{p.Name} (shared)"
            });
            _scopes.Add(new ScopeOption
            {
                DisplayName = $"Project: {p.Name}  (local)",
                FilePath = PermissionService.GetProjectLocalSettingsPath(p.FolderPath),
                ShortName = $"{p.Name} (local)"
            });
        }

        cmbScope.ItemsSource = _scopes;
        cmbScope.DisplayMemberPath = nameof(ScopeOption.DisplayName);
    }

    private void BuildToolDropdown()
    {
        cmbNewTool.ItemsSource = new[]
        {
            "Bash", "Read", "Edit", "Write", "WebFetch", "Agent", "Glob", "Grep"
        };
        cmbNewTool.SelectedIndex = 0;
    }

    private void BuildScopeDropdown()
    {
        cmbNewScope.ItemsSource = new[] { "Allow", "Deny", "Ask" };
        cmbNewScope.SelectedIndex = 0;
    }

    // ---- Loading ----

    private void LoadCurrent()
    {
        if (cmbScope.SelectedItem is not ScopeOption scope)
        {
            return;
        }

        try
        {
            _currentFile = PermissionFile.Load(scope.FilePath, scope.ShortName);
            txtFilePath.Text = scope.FilePath;
            RefreshGrid();

            // Load additional directories without firing dirty
            _suppressDirty = true;
            txtAdditionalDirs.Text = string.Join(Environment.NewLine, _currentFile.AdditionalDirectories);
            _suppressDirty = false;

            _isDirty = false;
            btnSave.IsEnabled = false;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to load permission file", ex);
            ShowError($"Failed to load {scope.FilePath}:\n{ex.Message}");
        }
    }

    private void RefreshGrid()
    {
        if (_currentFile == null)
        {
            return;
        }

        // Build view-models, then annotate each with cleanup level:
        //   2 = redundant (covered by a broader rule - delete candidate)
        //   1 = generalizable (overly-specific Bash - widen candidate, root cause of repeated prompts)
        //   0 = clean
        var vms = _currentFile.Rules.Select(r => new RuleViewModel(r)).ToList();

        foreach (var vm in vms)
        {
            var covering = _currentFile.Rules.FirstOrDefault(other => vm.Rule.IsCoveredBy(other));
            if (covering != null)
            {
                vm.WarningIcon = "\u26A0";
                vm.WarningTooltip = $"Redundant: covered by '{covering.RuleString}'.\nCan be safely deleted.";
                vm.WarningSort = 2;
                continue;
            }

            var suggested = vm.Rule.SuggestGeneralizedPattern();
            if (suggested != null)
            {
                vm.WarningIcon = "\u26A0";
                vm.WarningTooltip = $"Overly specific - this rule only matches one command.\nGeneralize to: {vm.Rule.Tool}({suggested})";
                vm.WarningSort = 1;
            }
        }

        // Sort: cleanup candidates first (highest WarningSort), then alpha by tool/pattern
        _viewRules = vms
            .OrderByDescending(vm => vm.WarningSort)
            .ThenBy(vm => vm.Tool, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vm => vm.PatternDisplay, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        var query = (txtFilter?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(query))
        {
            dgRules.ItemsSource = _viewRules;
            return;
        }

        dgRules.ItemsSource = _viewRules.Where(vm =>
            vm.Tool.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            vm.PatternDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            vm.ScopeDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    private void UpdateStatus()
    {
        var allow = _viewRules.Count(v => v.Rule.Scope == PermissionScope.Allow);
        var deny = _viewRules.Count(v => v.Rule.Scope == PermissionScope.Deny);
        var ask = _viewRules.Count(v => v.Rule.Scope == PermissionScope.Ask);
        var redundant = _viewRules.Count(v => v.WarningSort == 2);
        var generalizable = _viewRules.Count(v => v.WarningSort == 1);
        var dirty = _isDirty ? "   *" : "";

        var parts = new List<string> { $"Allow: {allow}", $"Deny: {deny}", $"Ask: {ask}" };
        if (redundant > 0)
        {
            parts.Add($"{redundant} redundant");
        }
        if (generalizable > 0)
        {
            parts.Add($"{generalizable} can be generalized");
        }
        txtStatus.Text = string.Join("  /  ", parts) + dirty;
    }

    private void MarkDirty()
    {
        if (_suppressDirty)
        {
            return;
        }
        _isDirty = true;
        btnSave.IsEnabled = true;
        UpdateStatus();
    }

    // ---- Event handlers ----

    private void CmbScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDirty)
        {
            var result = ConfirmDialog.Show(
                "Discard unsaved changes?",
                "You have unsaved permission changes. Discard them and switch scope?",
                "Discard", this);
            if (!result)
            {
                // Revert combo selection without re-firing this handler
                _suppressDirty = true;
                var prev = _scopes.FirstOrDefault(s => s.FilePath == _currentFile?.FilePath);
                if (prev != null)
                {
                    cmbScope.SelectedItem = prev;
                }
                _suppressDirty = false;
                return;
            }
        }

        LoadCurrent();
    }

    private void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        if (_isDirty)
        {
            var ok = ConfirmDialog.Show("Discard changes?",
                "Reload from disk and discard unsaved changes?", "Discard", this);
            if (!ok)
            {
                return;
            }
        }
        LoadCurrent();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void BtnGeneralizeAll_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null)
        {
            return;
        }

        // Find all rules where SuggestGeneralizedPattern returns non-null.
        // Whether the suggestion duplicates an existing broader rule doesn't matter
        // here - we mutate then dedupe at the bottom of the apply phase.
        var candidates = new List<(PermissionRule rule, string suggestion)>();
        foreach (var rule in _currentFile.Rules)
        {
            var suggested = rule.SuggestGeneralizedPattern();
            if (suggested == null)
            {
                continue;
            }
            candidates.Add((rule, suggested));
        }

        if (candidates.Count == 0)
        {
            ShowInfo("No rules to generalize.\n\nGeneralize works on Bash rules with multi-token patterns " +
                     "(e.g. 'npm install foo' → 'npm *') that aren't already wildcarded.");
            return;
        }

        static string Trunc(string s, int max) =>
            s.Length <= max ? s : s[..(max - 1)] + "\u2026";

        var preview = string.Join("\n", candidates.Take(20).Select(c =>
            $"  {c.rule.Tool}({Trunc(c.rule.Pattern ?? "", 60)})  \u2192  {c.rule.Tool}({c.suggestion})"));
        if (candidates.Count > 20)
        {
            preview += $"\n  ... and {candidates.Count - 20} more";
        }

        var ok = ConfirmDialog.Show(
            "Generalize all overly-specific rules?",
            $"This will widen {candidates.Count} rule(s):\n\n{preview}\n\n" +
            "Duplicates after generalization will be removed automatically.",
            "Generalize All", this);
        if (!ok)
        {
            return;
        }

        // Apply: change pattern to suggestion, then dedupe by (Scope, Tool, Pattern)
        foreach (var (rule, suggestion) in candidates)
        {
            rule.Pattern = suggestion;
        }

        var deduped = _currentFile.Rules
            .GroupBy(r => (r.Scope, r.Tool, r.Pattern ?? ""))
            .Select(g => g.First())
            .ToList();
        _currentFile.Rules.Clear();
        _currentFile.Rules.AddRange(deduped);

        MarkDirty();
        RefreshGrid();
    }

    private void DgRules_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Don't intercept Delete inside an in-cell editor (defensive - grid is
        // currently IsReadOnly, but cheap to guard against future edits).
        if (e.OriginalSource is System.Windows.Controls.TextBox)
        {
            return;
        }

        if (e.Key == Key.Delete && dgRules.SelectedItem is RuleViewModel)
        {
            MenuDelete_Click(sender, null!);
            e.Handled = true;
        }
    }

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null)
        {
            return;
        }

        var tool = cmbNewTool.SelectedItem as string ?? "";
        var pattern = txtNewPattern.Text?.Trim() ?? "";
        var scopeStr = cmbNewScope.SelectedItem as string ?? "Allow";

        if (string.IsNullOrWhiteSpace(tool))
        {
            ShowError("Pick a tool name.");
            return;
        }

        if (!Enum.TryParse<PermissionScope>(scopeStr, out var scope))
        {
            return;
        }

        var rule = new PermissionRule
        {
            Tool = tool,
            Pattern = string.IsNullOrEmpty(pattern) ? null : pattern,
            Scope = scope
        };

        // Reject obvious duplicates
        if (_currentFile.Rules.Any(r =>
                r.Scope == rule.Scope &&
                r.Tool == rule.Tool &&
                (r.Pattern ?? "") == (rule.Pattern ?? "")))
        {
            ShowError("This exact rule already exists in the same scope.");
            return;
        }

        _currentFile.Rules.Add(rule);
        txtNewPattern.Clear();
        MarkDirty();
        RefreshGrid();
    }

    private void MenuMove_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null || dgRules.SelectedItem is not RuleViewModel vm)
        {
            return;
        }

        if (sender is not System.Windows.Controls.MenuItem mi || mi.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse<PermissionScope>(tag, out var newScope))
        {
            return;
        }

        if (vm.Rule.Scope == newScope)
        {
            return;
        }

        vm.Rule.Scope = newScope;
        MarkDirty();
        RefreshGrid();
    }

    private void MenuGeneralize_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null || dgRules.SelectedItem is not RuleViewModel vm)
        {
            return;
        }

        var suggested = vm.Rule.SuggestGeneralizedPattern();
        if (suggested == null)
        {
            ShowError("This rule can't be generalized automatically.\n\n" +
                      "Generalize works on Bash rules with multi-token patterns " +
                      "(e.g. 'npm install foo' → 'npm *').");
            return;
        }

        var ok = ConfirmDialog.Show(
            "Generalize rule?",
            $"Replace:\n  {vm.Rule.Tool}({vm.Rule.Pattern})\n\nWith:\n  {vm.Rule.Tool}({suggested})\n\n" +
            "This widens the rule to match more commands of the same family.",
            "Generalize", this);
        if (!ok)
        {
            return;
        }

        vm.Rule.Pattern = suggested;
        MarkDirty();
        RefreshGrid();
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null || dgRules.SelectedItem is not RuleViewModel vm)
        {
            return;
        }

        var ok = ConfirmDialog.Show("Delete rule?",
            $"Delete this {vm.Rule.Scope} rule?\n\n  {vm.Rule.RuleString}",
            "Delete", this);
        if (!ok)
        {
            return;
        }

        _currentFile.Rules.Remove(vm.Rule);
        MarkDirty();
        RefreshGrid();
    }

    private void BtnPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null)
        {
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x2A, 0x3A)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x60, 0x60)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0))
        };

        foreach (var preset in PermissionService.Presets)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = preset.Name,
                ToolTip = preset.Description,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0))
            };
            var captured = preset;
            item.Click += (_, _) => ApplyPreset(captured);
            menu.Items.Add(item);
        }

        menu.PlacementTarget = btnPresets;
        menu.IsOpen = true;
    }

    private void ApplyPreset(PermissionPreset preset)
    {
        if (_currentFile == null)
        {
            return;
        }

        int added = 0;
        int skipped = 0;

        void TryAdd(string ruleStr, PermissionScope scope)
        {
            var rule = PermissionRule.Parse(ruleStr, scope);
            if (rule == null)
            {
                return;
            }

            var dup = _currentFile.Rules.Any(r =>
                r.Scope == rule.Scope &&
                r.Tool == rule.Tool &&
                (r.Pattern ?? "") == (rule.Pattern ?? ""));
            if (dup)
            {
                skipped++;
                return;
            }

            _currentFile.Rules.Add(rule);
            added++;
        }

        foreach (var r in preset.AllowRules)
        {
            TryAdd(r, PermissionScope.Allow);
        }
        foreach (var r in preset.DenyRules)
        {
            TryAdd(r, PermissionScope.Deny);
        }

        MarkDirty();
        RefreshGrid();
        ShowInfo($"Preset '{preset.Name}' applied.\n\nAdded: {added}\nSkipped (already present): {skipped}");
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile == null)
        {
            return;
        }

        try
        {
            // Sync additional directories textbox into the file model
            _currentFile.AdditionalDirectories.Clear();
            foreach (var line in (txtAdditionalDirs.Text ?? "").Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    _currentFile.AdditionalDirectories.Add(trimmed);
                }
            }

            _currentFile.Save();
            _isDirty = false;
            btnSave.IsEnabled = false;
            UpdateStatus();
            ShowInfo($"Saved to:\n{_currentFile.FilePath}\n\nA backup of the previous file was kept at:\n{_currentFile.FilePath}.bak");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to save permission file", ex);
            ShowError($"Failed to save:\n{ex.Message}");
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TxtAdditionalDirs_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkDirty();
    }

    private void TxtFilePath_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentFile == null || string.IsNullOrEmpty(_currentFile.FilePath))
        {
            return;
        }

        var path = _currentFile.FilePath;
        try
        {
            if (!File.Exists(path))
            {
                ShowInfo($"File doesn't exist yet:\n{path}\n\nIt will be created on first Save.");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open {path}: {ex.Message}");
            ShowError($"Couldn't open file:\n{ex.Message}");
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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
            "You have unsaved permission changes. Close anyway?", "Discard", this);
        if (!ok)
        {
            e.Cancel = true;
        }
    }

    // ---- Helpers ----

    private void ShowError(string message)
    {
        // Reuse confirm dialog as info: "OK" button, ignore result
        ConfirmDialog.Show("Permission Manager", message, "OK", this);
    }

    private void ShowInfo(string message)
    {
        ConfirmDialog.Show("Permission Manager", message, "OK", this);
    }

    // ---- Local types ----

    private class ScopeOption
    {
        public string DisplayName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ShortName { get; set; } = "";
    }

    public class RuleViewModel
    {
        public PermissionRule Rule { get; }

        public RuleViewModel(PermissionRule rule)
        {
            Rule = rule;
        }

        public string Tool => Rule.Tool;
        public string PatternDisplay => string.IsNullOrEmpty(Rule.Pattern) ? "(all)" : Rule.Pattern;
        public string ScopeDisplay => Rule.Scope.ToString();
        public string WarningIcon { get; set; } = "";
        public string WarningTooltip { get; set; } = "";
        public int WarningSort { get; set; }
    }
}
