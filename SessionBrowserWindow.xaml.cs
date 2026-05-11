using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class SessionBrowserWindow : Window
{
    private List<SessionTokenData> _allSessions = [];
    private readonly Dictionary<string, HashSet<string>> _branchCache = [];
    private readonly CompactAggressiveness _aggressiveness;
    private const string AllProjects = "(All)";

    // Content search state. _contentMatches is null when no search is active;
    // an empty dict means "search ran but matched nothing".
    private Dictionary<string, SessionContentMatch>? _contentMatches;
    private string? _contentSearchAppliedQuery;
    private CancellationTokenSource? _contentSearchCts;

    // Default column layout snapshot taken before any saved layout is
    // applied. Used by the "Restore default column layout" header menu so
    // users can undo accidental column resize/reorder without hunting for
    // the JSON file.
    private List<ColumnDefault> _defaultColumnLayout = [];
    private double _defaultWidth;
    private double _defaultHeight;

    private sealed record ColumnDefault(
        string Key,
        System.Windows.Controls.DataGridLength Width,
        int DisplayIndex);

    public SessionBrowserWindow(CompactAggressiveness aggressiveness = CompactAggressiveness.Balanced)
    {
        _aggressiveness = aggressiveness;
        InitializeComponent();
        // Capture XAML-declared defaults BEFORE any saved geometry overrides
        // them so "Restore default" can put the window back to its
        // shipped size.
        _defaultWidth = Width;
        _defaultHeight = Height;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        PopulateFilterCombos();
        txtSessionCount.Text = "Loading...";
        Loaded += async (_, _) =>
        {
            CaptureDefaultColumnLayout();
            // Read the layout file once and reuse for both apply paths to
            // avoid double-parsing JSON on every window open.
            var saved = SessionBrowserLayoutService.Load();
            ApplySavedColumnLayout(saved);
            ApplySavedWindowGeometry(saved);
            await LoadSessionsAsync();
        };
        Closed += (_, _) => SaveCurrentLayout();
    }

    /// <summary>
    /// Snapshot the XAML default widths + display order BEFORE any saved
    /// layout is applied. Lets "Restore default" return the grid to its
    /// pristine state regardless of what the user did.
    /// </summary>
    private void CaptureDefaultColumnLayout()
    {
        _defaultColumnLayout.Clear();
        foreach (var col in dgSessions.Columns)
        {
            var key = GetColumnKey(col);
            if (key == null)
            {
                continue;
            }
            _defaultColumnLayout.Add(new ColumnDefault(key, col.Width, col.DisplayIndex));
        }
    }

    /// <summary>
    /// True when the live grid OR the window size no longer matches the
    /// captured XAML defaults (user resized or reordered something, or
    /// resized the window). Used to grey-out the "Restore default layout"
    /// menu when there's nothing to undo.
    /// </summary>
    private bool IsLayoutModified()
    {
        if (_defaultColumnLayout.Count == 0)
        {
            return false;
        }

        if (Math.Abs(Width - _defaultWidth) > 1.0 ||
            Math.Abs(Height - _defaultHeight) > 1.0 ||
            WindowState != WindowState.Normal)
        {
            return true;
        }

        var liveByKey = new Dictionary<string, System.Windows.Controls.DataGridColumn>(StringComparer.Ordinal);
        foreach (var col in dgSessions.Columns)
        {
            var key = GetColumnKey(col);
            if (key != null)
            {
                liveByKey[key] = col;
            }
        }

        foreach (var def in _defaultColumnLayout)
        {
            if (!liveByKey.TryGetValue(def.Key, out var col))
            {
                continue;
            }
            if (col.DisplayIndex != def.DisplayIndex)
            {
                return true;
            }
            if (col.Width.UnitType != def.Width.UnitType)
            {
                return true;
            }
            if (!col.Width.IsStar && Math.Abs(col.Width.Value - def.Width.Value) > 0.5)
            {
                return true;
            }
        }
        return false;
    }

    private void RestoreDefaultLayout()
    {
        var liveByKey = new Dictionary<string, System.Windows.Controls.DataGridColumn>(StringComparer.Ordinal);
        foreach (var col in dgSessions.Columns)
        {
            var key = GetColumnKey(col);
            if (key != null)
            {
                liveByKey[key] = col;
            }
        }

        foreach (var def in _defaultColumnLayout)
        {
            if (!liveByKey.TryGetValue(def.Key, out var col))
            {
                continue;
            }
            if (col.CanUserResize)
            {
                col.Width = def.Width;
            }
        }

        // Display index pass — apply after widths so we don't fight the
        // grid's intermediate reorder layout.
        foreach (var def in _defaultColumnLayout)
        {
            if (!liveByKey.TryGetValue(def.Key, out var col))
            {
                continue;
            }
            if (def.DisplayIndex >= 0 && def.DisplayIndex < dgSessions.Columns.Count)
            {
                col.DisplayIndex = def.DisplayIndex;
            }
        }

        // Window size + state — return to XAML defaults too. Position is
        // intentionally left alone (don't recenter a window the user
        // dragged somewhere on purpose).
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }
        Width = _defaultWidth;
        Height = _defaultHeight;

        SaveCurrentLayout();
    }

    private void ColumnHeaderContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu)
        {
            return;
        }
        var modified = IsLayoutModified();
        foreach (var item in menu.Items)
        {
            if (item is System.Windows.Controls.MenuItem mi)
            {
                mi.IsEnabled = modified;
                mi.ToolTip = modified
                    ? null
                    : "Layout already matches defaults";
            }
        }
    }

    private void MenuRestoreLayout_Click(object sender, RoutedEventArgs e)
    {
        RestoreDefaultLayout();
    }

    /// <summary>
    /// Stable identifier for a column, set in XAML via the
    /// svc:DataGridColumnHelper.Key attached property. Header text was the
    /// previous strategy but broke on i18n / trailing whitespace.
    /// </summary>
    private static string? GetColumnKey(System.Windows.Controls.DataGridColumn col)
    {
        return DataGridColumnHelper.GetKey(col);
    }

    /// <summary>
    /// Restore column widths and display order from disk so user
    /// adjustments survive close/reopen. Unknown saved keys are ignored;
    /// columns missing from the save keep their XAML defaults.
    /// </summary>
    private void ApplySavedColumnLayout(SessionBrowserLayout? saved)
    {
        if (saved == null || saved.Columns.Count == 0)
        {
            return;
        }

        var byKey = new Dictionary<string, ColumnLayout>(StringComparer.Ordinal);
        foreach (var c in saved.Columns)
        {
            if (!string.IsNullOrEmpty(c.Tag))
            {
                byKey[c.Tag] = c;
            }
        }

        foreach (var col in dgSessions.Columns)
        {
            var key = GetColumnKey(col);
            if (key == null || !byKey.TryGetValue(key, out var savedCol))
            {
                continue;
            }
            if (col.CanUserResize)
            {
                col.Width = ParseWidth(savedCol.WidthValue, savedCol.WidthUnit, col.Width);
            }
        }

        foreach (var col in dgSessions.Columns)
        {
            var key = GetColumnKey(col);
            if (key == null || !byKey.TryGetValue(key, out var savedCol))
            {
                continue;
            }
            if (savedCol.DisplayIndex >= 0 && savedCol.DisplayIndex < dgSessions.Columns.Count)
            {
                col.DisplayIndex = savedCol.DisplayIndex;
            }
        }
    }

    private void SaveCurrentLayout()
    {
        try
        {
            var layout = new SessionBrowserLayout
            {
                Window = CaptureWindowGeometry()
            };
            foreach (var col in dgSessions.Columns)
            {
                var key = GetColumnKey(col);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }
                layout.Columns.Add(new ColumnLayout
                {
                    Tag = key,
                    WidthValue = col.ActualWidth,
                    WidthUnit = col.Width.IsStar ? "Star" : "Pixel",
                    DisplayIndex = col.DisplayIndex
                });
            }
            SessionBrowserLayoutService.Save(layout);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to save layout: {ex.Message}");
        }
    }

    private WindowGeometry CaptureWindowGeometry()
    {
        // RestoreBounds is what the window would shrink back to after
        // unmaximising — so even when the user closes while maximised,
        // we save the underlying restored size, not the monitor size.
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new System.Windows.Rect(Left, Top, Width, Height);
        return new WindowGeometry
        {
            Width = bounds.Width,
            Height = bounds.Height,
            Left = double.IsNaN(bounds.Left) ? null : bounds.Left,
            Top = double.IsNaN(bounds.Top) ? null : bounds.Top,
            State = WindowState == WindowState.Maximized ? "Maximized" : "Normal"
        };
    }

    private void ApplySavedWindowGeometry(SessionBrowserLayout? saved)
    {
        var geom = saved?.Window;
        if (geom == null)
        {
            return;
        }

        if (geom.Width >= MinWidth && geom.Width <= 4000)
        {
            Width = geom.Width;
        }
        if (geom.Height >= MinHeight && geom.Height <= 3000)
        {
            Height = geom.Height;
        }

        if (geom.Left.HasValue && geom.Top.HasValue &&
            IsPointOnAnyScreen(geom.Left.Value, geom.Top.Value))
        {
            // Switch off CenterScreen positioning so our coords stick.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = geom.Left.Value;
            Top = geom.Top.Value;
        }

        if (geom.State == "Maximized")
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Guards against restoring to a screen that's no longer connected
    /// (laptop docked → undocked, second monitor unplugged) which would
    /// strand the window off-screen. Uses WPF's virtual-screen rect which
    /// is already in DIPs (no DPI conversion needed) — Screen.AllScreens
    /// would return physical pixels and break on mixed-DPI multi-monitor
    /// setups (4K laptop + 1080p external is the common case).
    /// </summary>
    private static bool IsPointOnAnyScreen(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return false;
        }
        // ~50/20 DIP slop so partial off-screen titlebars still count as visible.
        var x = left + 50;
        var y = top + 20;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        return x >= virtualLeft && x <= virtualRight
            && y >= virtualTop && y <= virtualBottom;
    }

    private static System.Windows.Controls.DataGridLength ParseWidth(double value, string unit, System.Windows.Controls.DataGridLength fallback)
    {
        if (unit == "Star")
        {
            // The Star value carries weight; on save we capture ActualWidth
            // (pixel), but that value is meaningless as a Star ratio. Keep
            // the original Star width on restore so the * column still
            // fills remaining space unless the user explicitly resized it.
            return fallback;
        }
        if (value > 0)
        {
            return new System.Windows.Controls.DataGridLength(value);
        }
        return fallback;
    }

    private void PopulateFilterCombos()
    {
        cmbDays.Items.Add(new ComboBoxItem { Content = "7", Tag = 7 });
        cmbDays.Items.Add(new ComboBoxItem { Content = "14", Tag = 14 });
        cmbDays.Items.Add(new ComboBoxItem { Content = "30", Tag = 30 });
        cmbDays.Items.Add(new ComboBoxItem { Content = "90", Tag = 90 });
        cmbDays.Items.Add(new ComboBoxItem { Content = "All", Tag = 0 });
        cmbDays.SelectedIndex = 2; // 30 days default

        cmbColor.Items.Add(new ComboBoxItem { Content = "(All)" });
        foreach (var color in SessionColorService.AvailableColors)
        {
            cmbColor.Items.Add(new ComboBoxItem { Content = color });
        }
        cmbColor.SelectedIndex = 0;
    }

    private void LoadSessions()
    {
        Cursor = Cursors.Wait;
        _allSessions = SessionWatcherService.ScanAllSessions();
        RebuildProjectFilter();
        ApplyFilters();
        Cursor = Cursors.Arrow;
    }

    private async Task LoadSessionsAsync()
    {
        loadingOverlay.Visibility = Visibility.Visible;
        _allSessions = await Task.Run(() => SessionWatcherService.ScanAllSessions());
        RebuildProjectFilter();
        ApplyFilters();
        loadingOverlay.Visibility = Visibility.Collapsed;
    }

    private void RebuildProjectFilter()
    {
        var currentSelection = (cmbProject.SelectedItem as ComboBoxItem)?.Content?.ToString();

        cmbProject.Items.Clear();
        cmbProject.Items.Add(new ComboBoxItem { Content = AllProjects });

        var projects = _allSessions
            .Select(s => s.ProjectName)
            .Distinct()
            .OrderBy(p => p);

        foreach (var project in projects)
        {
            cmbProject.Items.Add(new ComboBoxItem { Content = project });
        }

        // Restore selection or default to All
        var found = false;
        if (currentSelection != null)
        {
            for (int i = 0; i < cmbProject.Items.Count; i++)
            {
                if (((ComboBoxItem)cmbProject.Items[i]).Content?.ToString() == currentSelection)
                {
                    cmbProject.SelectedIndex = i;
                    found = true;
                    break;
                }
            }
        }
        if (!found)
        {
            cmbProject.SelectedIndex = 0;
        }
    }

    private void ApplyFilters()
    {
        var projectFilter = (cmbProject.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? AllProjects;
        var daysTag = (cmbDays.SelectedItem as ComboBoxItem)?.Tag;
        var days = daysTag is int d ? d : 30;
        var staleOnly = chkStaleOnly.IsChecked == true;
        var searchText = txtSearch?.Text?.Trim() ?? "";

        var filtered = _allSessions.AsEnumerable();

        if (projectFilter != AllProjects)
        {
            filtered = filtered.Where(s => s.ProjectName == projectFilter);
        }

        if (days > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            filtered = filtered.Where(s => s.LastSeen > cutoff);
        }

        var viewModels = filtered.Select(s =>
        {
            var vm = new SessionViewModel(s, _aggressiveness);
            if (_contentMatches != null && _contentMatches.TryGetValue(s.SessionId, out var m))
            {
                vm.ContentMatch = m;
            }
            return vm;
        }).ToList();

        if (staleOnly)
        {
            Cursor = Cursors.Wait;
            viewModels = viewModels.Where(vm => IsStale(vm)).ToList();
            Cursor = Cursors.Arrow;
        }

        if (!string.IsNullOrEmpty(searchText))
        {
            viewModels = viewModels
                .Where(vm => vm.Topic.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    || (vm.GitBranch?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true)
                    || vm.ProjectName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                    || (vm.Note?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true))
                .ToList();
        }

        if (_contentMatches != null)
        {
            viewModels = viewModels
                .Where(vm => _contentMatches.ContainsKey(vm.SessionId))
                .ToList();
        }

        var colorFilter = (cmbColor?.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (colorFilter != null && colorFilter != "(All)")
        {
            viewModels = viewModels.Where(vm => vm.ColorTag == colorFilter).ToList();
        }

        // Sort favorites to top
        viewModels = viewModels
            .OrderByDescending(vm => vm.IsFavorite)
            .ThenByDescending(vm => vm.LastSeen)
            .ToList();

        dgSessions.ItemsSource = viewModels;
        UpdateStatistics(viewModels);
    }

    private void UpdateStatistics(List<SessionViewModel> viewModels)
    {
        txtSessionCount.Text = $"{viewModels.Count} session{(viewModels.Count == 1 ? "" : "s")}";

        if (viewModels.Count == 0)
        {
            txtStats.Text = "";
            return;
        }

        var totalIn = viewModels.Sum(vm => vm.TotalIn);
        var totalOut = viewModels.Sum(vm => vm.OutputTokens);
        var totalDuration = TimeSpan.FromMinutes(viewModels.Sum(vm => vm.DurationMinutes));
        var totalTurns = viewModels.Sum(vm => vm.TurnCount);

        var durationText = totalDuration.TotalHours >= 1
            ? $"{(int)totalDuration.TotalHours}h {totalDuration.Minutes:D2}m"
            : $"{(int)totalDuration.TotalMinutes}m";

        var earliest = viewModels.Min(vm => vm.LastSeen);
        var span = DateTimeOffset.UtcNow - earliest;
        var spanText = span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d"
            : $"{(int)span.TotalHours}h";

        txtStats.Text = $"Totals ({spanText}):  {FormatTokens(totalIn)} in  /  {FormatTokens(totalOut)} out  |  {totalTurns} turns  |  {durationText} active";
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
        >= 1_000 => $"{tokens / 1_000.0:F1}k",
        _ => tokens.ToString()
    };

    private bool IsStale(SessionViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.GitBranch) || string.IsNullOrEmpty(vm.Cwd))
        {
            return false;
        }

        if (!_branchCache.TryGetValue(vm.Cwd, out var branches))
        {
            branches = GetGitBranches(vm.Cwd);
            _branchCache[vm.Cwd] = branches;
        }

        return branches.Count > 0 && !branches.Contains(vm.GitBranch);
    }

    private void CmbProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            // Project change shifts content-search scope — drop stale matches.
            ClearContentSearchResults();
            ApplyFilters();
        }
    }

    private void CmbDays_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ClearContentSearchResults();
            ApplyFilters();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _branchCache.Clear();
        ClearContentSearchResults();
        txtSessionCount.Text = "Loading...";
        await LoadSessionsAsync();
    }

    private void ChkStaleOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilters();
        }
    }

    private static HashSet<string> GetGitBranches(string cwd)
    {
        if (!Directory.Exists(cwd))
        {
            return [];
        }

        try
        {
            var psi = new ProcessStartInfo("git", "branch --list --format=%(refname:short)")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet();
        }
        catch
        {
            return [];
        }
    }

    private void DgSessions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ResumeSelected();
    }

    private void BtnResume_Click(object sender, RoutedEventArgs e)
    {
        ResumeSelected();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = dgSessions.SelectedItems.Cast<SessionViewModel>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var msg = selected.Count == 1
            ? $"Delete session \"{selected[0].Topic}\"?\n\nThis will permanently remove the session file from disk."
            : $"Delete {selected.Count} sessions?\n\nThis will permanently remove the session files from disk.";

        if (!ConfirmDialog.Show("Delete Sessions", msg, "Delete", this))
        {
            return;
        }

        foreach (var vm in selected)
        {
            try
            {
                if (File.Exists(vm.FilePath))
                {
                    File.Delete(vm.FilePath);
                }

                var dir = Path.Combine(
                    Path.GetDirectoryName(vm.FilePath) ?? "",
                    Path.GetFileNameWithoutExtension(vm.FilePath));
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // File may be locked by active session
            }
        }

        LoadSessions();
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilters();
        }
    }

    private async void TxtContentSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunContentSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelContentSearch();
            txtContentSearch.Text = "";
            ClearContentSearchResults();
        }
    }

    private void BtnContentSearchCancel_Click(object sender, RoutedEventArgs e)
    {
        CancelContentSearch();
    }

    private void CancelContentSearch()
    {
        _contentSearchCts?.Cancel();
    }

    private void ClearContentSearchResults()
    {
        if (_contentMatches == null && _contentSearchAppliedQuery == null)
        {
            return;
        }
        _contentMatches = null;
        _contentSearchAppliedQuery = null;
        txtContentSearchStatus.Text = "";
        ApplyFilters();
    }

    private async Task RunContentSearchAsync()
    {
        var query = txtContentSearch.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ClearContentSearchResults();
            return;
        }

        // Re-running the same query is a no-op once results are cached.
        if (_contentSearchAppliedQuery == query && _contentMatches != null)
        {
            return;
        }

        // Cancel any in-flight search and dispose its CTS so we don't leak
        // handles when the user runs many searches in sequence.
        var oldCts = _contentSearchCts;
        var cts = new CancellationTokenSource();
        _contentSearchCts = cts;
        if (oldCts != null)
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }
        var ct = cts.Token;

        // Build candidate list using current Project + Days pre-filter.
        var projectFilter = (cmbProject.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? AllProjects;
        var daysTag = (cmbDays.SelectedItem as ComboBoxItem)?.Tag;
        var days = daysTag is int d ? d : 30;

        var candidates = _allSessions.AsEnumerable();
        if (projectFilter != AllProjects)
        {
            candidates = candidates.Where(s => s.ProjectName == projectFilter);
        }
        if (days > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            candidates = candidates.Where(s => s.LastSeen > cutoff);
        }

        var list = candidates
            .Select(s => (SessionId: s.SessionId, FilePath: s.FilePath))
            .ToList();

        if (list.Count == 0)
        {
            txtContentSearchStatus.Text = "No sessions in current scope";
            _contentMatches = new Dictionary<string, SessionContentMatch>();
            _contentSearchAppliedQuery = query;
            ApplyFilters();
            return;
        }

        btnContentSearchCancel.Visibility = Visibility.Visible;
        txtContentSearch.IsEnabled = false;
        txtContentSearchStatus.Text = $"Searching 0 / {list.Count}...";

        var progress = new Progress<int>(processed =>
        {
            txtContentSearchStatus.Text = $"Searching {processed} / {list.Count}...";
        });

        try
        {
            var matches = await Task.Run(() => SessionContentSearchService.SearchAsync(
                list, query, progress, ct), ct);

            _contentMatches = matches;
            _contentSearchAppliedQuery = query;
            txtContentSearchStatus.Text = matches.Count == 1
                ? "1 match"
                : $"{matches.Count} matches";
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
            txtContentSearchStatus.Text = "Cancelled";
            // Leave previous results in place.
        }
        catch (Exception ex)
        {
            AppLogger.Error("Content search failed", ex);
            txtContentSearchStatus.Text = "Search failed";
        }
        finally
        {
            // Only the most recent search may touch UI state. If a newer search
            // already started while this one was running (or finishing), let
            // the newer one own the disable/cancel button.
            if (_contentSearchCts == cts)
            {
                btnContentSearchCancel.Visibility = Visibility.Collapsed;
                txtContentSearch.IsEnabled = true;
                txtContentSearch.Focus();
            }
        }
    }

    private void Star_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SessionViewModel vm)
        {
            FavoritesService.Toggle(vm.SessionId);
            ApplyFilters();
            e.Handled = true;
        }
    }

    private void MenuColor_Click(object sender, RoutedEventArgs e)
    {
        if (dgSessions.SelectedItem is not SessionViewModel vm)
        {
            return;
        }

        var tag = (sender as System.Windows.Controls.MenuItem)?.Tag?.ToString();
        SessionColorService.SetColor(vm.SessionId, string.IsNullOrEmpty(tag) ? null : tag);
        ApplyFilters();
    }

    private void CmbColor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilters();
        }
    }

    private void MenuToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (dgSessions.SelectedItem is SessionViewModel vm)
        {
            FavoritesService.Toggle(vm.SessionId);
            ApplyFilters();
        }
    }

    private void MenuEditNote_Click(object sender, RoutedEventArgs e)
    {
        if (dgSessions.SelectedItem is not SessionViewModel vm)
        {
            return;
        }

        var label = string.IsNullOrEmpty(vm.ProjectName)
            ? vm.Topic
            : $"{vm.ProjectName} — {vm.Topic}";
        var (saved, note) = NoteEditorDialog.Show(label, vm.Note, this);
        if (!saved)
        {
            return;
        }

        SessionNotesService.SetNote(vm.SessionId, note);
        ApplyFilters();
    }

    private void MenuExport_Click(object sender, RoutedEventArgs e)
    {
        if (dgSessions.SelectedItem is not SessionViewModel vm)
        {
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Session archive (*.zip)|*.zip",
            FileName = $"claude-session-{vm.SessionId[..8]}.zip",
            DefaultExt = ".zip"
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SessionSharingService.Export(vm.FilePath, dlg.FileName);
            txtStats.Text = $"Exported session to {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            txtStats.Text = $"Export failed: {ex.Message}";
        }
    }

    private void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Session archive (*.zip)|*.zip",
            DefaultExt = ".zip"
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        // Ask for target project folder
        var folderDlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the target project folder for the imported session",
            UseDescriptionForTitle = true
        };

        if (folderDlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var (success, message) = SessionSharingService.Import(dlg.FileName, folderDlg.SelectedPath);

        if (success)
        {
            txtStats.Text = "Session imported successfully";
            LoadSessions();
        }
        else
        {
            ConfirmDialog.Show("Import", message, "OK", this);
        }
    }

    private void MenuCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (dgSessions.SelectedItem is SessionViewModel vm)
        {
            Clipboard.SetText(vm.SessionId);
        }
    }

    private void ResumeSelected()
    {
        if (dgSessions.SelectedItem is not SessionViewModel vm)
        {
            return;
        }

        var projectRoot = ProjectKeyToPath(vm.FilePath);

        var entry = new QuickLaunchEntry
        {
            Name = vm.ProjectName,
            FolderPath = projectRoot ?? vm.Cwd ?? ""
        };

        ClaudeLauncherService.LaunchResume(entry, vm.SessionId);
    }

    internal static string? ProjectKeyToPath(string jsonlFilePath)
    {
        var projectDir = Path.GetDirectoryName(jsonlFilePath);
        if (projectDir == null)
        {
            return null;
        }

        var key = Path.GetFileName(projectDir);
        var decoded = DecodeProjectKey(key);
        if (decoded != null && Directory.Exists(decoded))
        {
            return decoded;
        }

        return null;
    }

    internal static string? DecodeProjectKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        // Key format: "C--src-Dev" → "C:\src\Dev"
        // First char + "--" = drive letter with colon, remaining "-" = path separators.
        // Known limitation: folders with literal dashes (e.g. "ExergyERP-Dev") are
        // indistinguishable from path separators. Directory.Exists in the caller guards
        // against incorrect decodes.
        if (key.Length >= 3 && key[1] == '-' && key[2] == '-' && char.IsLetter(key[0]))
        {
            var rest = key.Substring(3).Replace('-', Path.DirectorySeparatorChar);
            return $"{key[0]}:{Path.DirectorySeparatorChar}{rest}";
        }

        return null;
    }
}

public class SessionViewModel
{
    private readonly SessionTokenData _data;
    private readonly CompactAggressiveness _aggressiveness;

    public SessionViewModel(SessionTokenData data, CompactAggressiveness aggressiveness = CompactAggressiveness.Balanced)
    {
        _data = data;
        _aggressiveness = aggressiveness;
    }

    public string SessionId => _data.SessionId;
    public string FilePath => _data.FilePath;
    public string ProjectName => _data.ProjectName;
    public string CwdPath => _data.Cwd ?? _data.FilePath;
    public string? GitBranch => _data.GitBranch;
    public string? Cwd => _data.Cwd;
    public int TurnCount => _data.TurnCount;
    public long OutputTokens => _data.OutputTokens;
    public long TotalIn => _data.InputTokens + _data.CacheReadTokens + _data.CacheCreationTokens;
    public DateTimeOffset LastSeen => _data.LastSeen;
    public bool IsFavorite => FavoritesService.IsFavorite(_data.SessionId);
    /// <summary>Always show a star \u2014 solid (gold) for favorites, hollow
    /// (greyed out) for non-favorites. Empty cells made the click target
    /// invisible until users stumbled onto it by accident.</summary>
    public string FavoriteIcon => IsFavorite ? "\u2605" : "\u2606";
    /// <summary>
    /// Frozen brushes shared across all rows. Without this, the binding
    /// would allocate fresh SolidColorBrush instances every time a row
    /// repaints, taking the slow per-thread path because the brush isn't
    /// freezable.
    /// </summary>
    private static readonly System.Windows.Media.SolidColorBrush FavoriteGoldBrush =
        CreateFrozenBrush(0xFF, 0xD7, 0x00);
    private static readonly System.Windows.Media.SolidColorBrush FavoriteGreyBrush =
        CreateFrozenBrush(0x50, 0x50, 0x60);

    private static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public System.Windows.Media.SolidColorBrush FavoriteForeground =>
        IsFavorite ? FavoriteGoldBrush : FavoriteGreyBrush;

    public string? ColorTag => SessionColorService.GetColor(_data.SessionId);

    public string? Note => SessionNotesService.GetNote(_data.SessionId);
    public bool HasNote => !string.IsNullOrEmpty(Note);

    public System.Windows.Media.SolidColorBrush ColorForeground
    {
        get
        {
            if (ColorTag != null && SessionColorService.ColorHexMap.TryGetValue(ColorTag, out var hex))
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
                return new System.Windows.Media.SolidColorBrush(color);
            }
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0));
        }
    }

    private CompactRecommendation? _recommendation;
    private CompactRecommendation Recommendation => _recommendation ??= _data.GetRecommendation(_aggressiveness);

    public string HintIcon => Recommendation.Level switch
    {
        CompactLevel.Critical => "\u26A0",
        CompactLevel.Warning => "\u26A0",
        CompactLevel.Hint => "\u2139",
        _ => ""
    };

    public string HintTooltip => Recommendation.Level == CompactLevel.None
        ? ""
        : string.Join("\n", Recommendation.Reasons);

    public string Topic => _data.Topic;

    /// <summary>Content-search match for this session, if any. Set by the
    /// browser after running a search; surfaced in the Topic tooltip so
    /// users can tell at a glance whether the hit is a real discussion or
    /// a passing mention.</summary>
    public SessionContentMatch? ContentMatch { get; set; }

    public string TopicFull
    {
        get
        {
            var parts = new List<string>();
            if (_data.CustomName != null)
            {
                parts.Add($"Name: {_data.CustomName}");
            }
            if (_data.FirstMessage != null)
            {
                parts.Add($"First message: {_data.FirstMessage}");
            }
            if (_data.Slug != null)
            {
                parts.Add($"Slug: {_data.Slug}");
            }
            parts.Add($"ID: {_data.SessionId}");

            var note = Note;
            if (!string.IsNullOrEmpty(note))
            {
                parts.Add("");
                parts.Add("--- Note ---");
                parts.Add(note);
            }

            if (ContentMatch != null)
            {
                var label = ContentMatch.Source switch
                {
                    SessionContentMatchSource.User => "user",
                    SessionContentMatchSource.Assistant => "assistant",
                    SessionContentMatchSource.CustomTitle => "custom title",
                    _ => "match"
                };
                parts.Add("");
                parts.Add($"--- Search match ({label}) ---");
                parts.Add(ContentMatch.Excerpt);
            }
            return string.Join("\n", parts);
        }
    }

    // Raw percentage — kept uncapped for sorting (so two anomalous sessions sort
    // by their true magnitude) and for diagnostics. The DISPLAYED value is capped
    // by ContextText below because a real Anthropic API prompt cannot exceed the
    // model's context window — anything >100% means our accounting or the upstream
    // usage report is off, and a misleading literal "306%" damages trust more
    // than honestly flagging the anomaly.
    public int ContextPct => _data.ContextWindowSize > 0 && _data.LastTurnContextSize > 0
        ? (int)(100.0 * _data.LastTurnContextSize / _data.ContextWindowSize)
        : 0;

    public string ContextText
    {
        get
        {
            if (ContextPct <= 0) return "";
            return ContextPct > 100 ? "100%+" : $"{ContextPct}%";
        }
    }

    public double DurationMinutes => _data.ActiveDuration.TotalMinutes;

    public string DurationText
    {
        get
        {
            var d = _data.ActiveDuration;
            if (d.TotalSeconds < 30)
            {
                return "";
            }
            if (d.TotalMinutes < 60)
            {
                return $"{(int)d.TotalMinutes}m";
            }
            return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
        }
    }

    /// <summary>
    /// Compact display name for the model. Prefix-based so new versions
    /// (claude-opus-4-7, claude-sonnet-5-0, etc.) render nicely without
    /// code changes — "claude-opus-4-7" -> "Opus 4.7".
    /// </summary>
    public string ModelShort
    {
        get
        {
            var m = _data.Model;
            if (string.IsNullOrEmpty(m))
            {
                return "";
            }
            if (TryFormatFamily(m, "claude-opus-", "Opus", out var opus))
            {
                return opus;
            }
            if (TryFormatFamily(m, "claude-sonnet-", "Sonnet", out var sonnet))
            {
                return sonnet;
            }
            if (TryFormatFamily(m, "claude-haiku-", "Haiku", out var haiku))
            {
                return haiku;
            }
            return m.Replace("claude-", "");
        }
    }

    private static bool TryFormatFamily(string model, string prefix, string label, out string formatted)
    {
        formatted = "";
        if (!model.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        // "claude-opus-4-7" -> "4-7" -> "4.7"
        var version = model.Substring(prefix.Length).Replace('-', '.');
        formatted = string.IsNullOrEmpty(version) ? label : $"{label} {version}";
        return true;
    }

    public string LastActiveText
    {
        get
        {
            var ago = DateTimeOffset.UtcNow - _data.LastSeen;
            if (ago.TotalMinutes < 10)
            {
                return "Active now";
            }
            if (ago.TotalHours < 1)
            {
                return $"{(int)ago.TotalMinutes}m ago";
            }
            if (ago.TotalHours < 24)
            {
                return $"{(int)ago.TotalHours}h ago";
            }
            if (ago.TotalDays < 7)
            {
                return $"{(int)ago.TotalDays}d ago";
            }
            return _data.LastSeen.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    public string TokensInText => FormatTokens(TotalIn);
    public string TokensOutText => FormatTokens(OutputTokens);

    /// <summary>
    /// Tooltip for the "In" column — breaks the lump-sum total into its three
    /// pricing-relevant buckets. Most of `TotalIn` for any heavy session is
    /// cache reads (the model re-reading conversation history each turn);
    /// those bill at roughly 1/10 the rate of fresh input. Showing just the
    /// total without a breakdown made users think the cost was 10× what it
    /// actually was.
    /// </summary>
    public string TokensInTooltip
    {
        get
        {
            var fresh = _data.InputTokens;
            var read = _data.CacheReadTokens;
            var write = _data.CacheCreationTokens;
            var total = fresh + read + write;
            var readPct = total > 0 ? 100.0 * read / total : 0;
            return
                $"Total prompt tokens: {FormatTokens(total)}\n" +
                $"\n" +
                $"  Fresh input:     {FormatTokens(fresh),10}\n" +
                $"  Cache reads:     {FormatTokens(read),10}   ({readPct:F0}% of total — billed at ~0.1× rate)\n" +
                $"  Cache writes:    {FormatTokens(write),10}\n" +
                $"\n" +
                $"Cache reads dominate long sessions because Claude re-reads the\n" +
                $"conversation history each turn. They're cheap; the headline\n" +
                $"number looks scarier than the actual cost.";
        }
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
        >= 1_000 => $"{tokens / 1_000.0:F1}k",
        _ => tokens.ToString()
    };
}
