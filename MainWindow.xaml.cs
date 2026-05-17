using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using System.Windows.Threading;
using Microsoft.Win32;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly ClaudeUsageService _usageService = new();
    private readonly SessionWatcherService _sessionWatcher = new();
    private readonly PermissionWatcherService _permissionWatcher = new();
    private readonly UpdateService _updateService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _countdownTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ToolStripMenuItem? _hotkeyMenuItem;
    private bool _hotkeyRegistered;
    private bool _sessionsExpanded;
    private SessionBrowserWindow? _sessionBrowserWindow;
    private PermissionManagerWindow? _permissionManagerWindow;
    private ClaudeConfigWindow? _claudeConfigWindow;
    private AgentsSkillsWindow? _agentsSkillsWindow;
    private AppSettings _appSettings = new();
    private readonly HashSet<string> _notifiedSessions = [];
    private readonly Dictionary<int, QuickLaunchEntry> _quickLaunchHotkeys = [];
    private LowLevelKeyboardHook? _keyboardHook;

    // Binding IDs returned by LowLevelKeyboardHook.Register
    private int _widgetBindingId = -1;
    private int _sessionBrowserBindingId = -1;
    private int _promptLibraryBindingId = -1;
    private int _permissionManagerBindingId = -1;
    private int _claudeConfigBindingId = -1;
    private int _agentsSkillsBindingId = -1;

    public MainWindow()
    {
        InitializeComponent();

        // Timer ticks frequently but ClaudeUsageService has its own adaptive cache
        // so we only actually hit the API when the cache is stale.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += async (_, _) =>
        {
            try { await SafeRefreshAsync(); }
            catch (Exception ex) { AppLogger.Error("RefreshTimer tick failed", ex); }
        };

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _countdownTimer.Tick += (_, _) =>
        {
            try { UpdateCountdowns(); }
            catch (Exception ex) { AppLogger.Error("CountdownTimer tick failed", ex); }
        };

        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("Widget starting up");
        PositionBottomRight();
        _appSettings = SettingsService.Load();
        ApplyLauncherSettings();
        SetupTrayIcon();
        SetupHotkey();

        _sessionWatcher.DataChanged += () =>
            Dispatcher.BeginInvoke(UpdateTokenUI);
        _sessionWatcher.Start();

        // When Claude Code refreshes the OAuth token, immediately fetch fresh data
        // instead of waiting up to 30s for the next refresh timer tick.
        // The watcher fires on a thread-pool thread; marshal to UI thread before
        // touching service state to avoid races with FetchUsageAsync.
        _usageService.CredentialsRefreshed += () =>
            Dispatcher.BeginInvoke(async () =>
            {
                _usageService.ClearBackoffOnCredentialsRefresh();
                AppLogger.Info("Triggering immediate refresh after credentials change");
                await SafeRefreshAsync(forceRefresh: true);
            });

        if (_appSettings.EnablePermissionSuggestions)
        {
            _permissionWatcher.SuggestionReady += suggestion =>
                Dispatcher.BeginInvoke(() => ShowPermissionSuggestion(suggestion));
            _permissionWatcher.Start(_appSettings.QuickLaunchEntries);
        }

        if (IsHotkeyEnabled())
        {
            RegisterQuickLaunchHotkeys();
        }

        await SafeRefreshAsync();
        UpdateTokenUI();
        ApplyViewMode();
        _refreshTimer.Start();
        _countdownTimer.Start();

        // Fire-and-forget update check — delayed so we don't compete with the
        // initial Claude usage fetch, and silent unless something is available.
        _ = CheckForUpdatesBackgroundAsync();
    }

    private readonly System.Threading.CancellationTokenSource _updateCheckCts = new();

    // Skip the auto-check unless this many hours have passed since the last
    // one. Without this, every update + restart cycle (5 updates landed
    // today => 5 toasts) pesters the user with a fresh "update available"
    // toast each time the app starts. Manual "Check for updates..." from
    // the tray menu always bypasses this throttle.
    private static readonly TimeSpan UpdateCheckThrottle = TimeSpan.FromHours(8);

    private async Task CheckForUpdatesBackgroundAsync()
    {
        // Master switch: user can fully opt out via Settings.
        if (!_appSettings.CheckForUpdatesOnStartup)
        {
            return;
        }

        // Throttle: skip if a check happened recently.
        if (_appSettings.LastUpdateCheckUtc.HasValue
            && DateTimeOffset.UtcNow - _appSettings.LastUpdateCheckUtc.Value < UpdateCheckThrottle)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _updateCheckCts.Token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!_updateService.IsInstalled)
        {
            // Running from `dotnet run` or a standalone exe — no Velopack
            // bundle, so updates aren't applicable. Stay silent.
            return;
        }

        var update = await _updateService.CheckForUpdatesAsync();

        // Mark the throttle window opened regardless of whether anything
        // was found — a "no update" check still counts against the cooldown.
        _appSettings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
        try
        {
            SettingsService.Save(_appSettings);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Could not persist LastUpdateCheckUtc: {ex.Message}");
        }
        if (update == null || _updateCheckCts.IsCancellationRequested)
        {
            return;
        }

        // Window may be closing between the check and the dispatch; skip the
        // balloon if the dispatcher has started shutting down.
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        await Dispatcher.BeginInvoke(() =>
        {
            _trayIcon?.ShowBalloonTip(
                10_000,
                "Update available",
                $"Version {update.TargetFullRelease.Version} is ready. Right-click the tray icon → Check for updates… to install.",
                System.Windows.Forms.ToolTipIcon.Info);
        });
    }

    private async Task CheckForUpdatesInteractiveAsync()
    {
        if (!_updateService.IsInstalled)
        {
            System.Windows.MessageBox.Show(
                "Updates are managed via Velopack and only available on builds installed from a release package.\n\n" +
                "This looks like a development build (run from source). Use the latest release from GitHub instead.",
                "Updates unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var update = await _updateService.CheckForUpdatesAsync();
        if (update == null)
        {
            System.Windows.MessageBox.Show(
                "You're on the latest version.",
                "No updates",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var go = System.Windows.MessageBox.Show(
            $"Version {update.TargetFullRelease.Version} is available.\n\nDownload and install now? The app will restart automatically.",
            "Update available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (go != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _updateService.DownloadUpdatesAsync(update);

            // Belt-and-braces flush before Velopack tears the process down —
            // anything mutated since the last explicit Save would otherwise
            // be lost across the restart. Also stop the low-level keyboard
            // hook so the replacement process's hook registration doesn't
            // race with a still-alive one.
            try
            {
                SettingsService.Save(_appSettings);
            }
            catch (Exception saveEx)
            {
                AppLogger.Warn($"Pre-update settings flush failed: {saveEx.Message}");
            }
            _keyboardHook?.Dispose();
            _keyboardHook = null;

            _updateService.ApplyAndRestart(update);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Update install failed", ex);
            System.Windows.MessageBox.Show(
                $"Update failed: {ex.Message}\n\nSee the log for details.",
                "Update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SetupHotkey()
    {
        _keyboardHook = new LowLevelKeyboardHook();
        _keyboardHook.Install();

        if (IsHotkeyEnabled())
        {
            RegisterAllHotkeys();
        }
    }

    private void RegisterAllHotkeys()
    {
        RegisterGlobalHotkey();
        _sessionBrowserBindingId = _keyboardHook!.Register(_appSettings.SessionBrowserHotkey, ShowSessionBrowser);
        _promptLibraryBindingId = _keyboardHook.Register(_appSettings.PromptLibraryHotkey, ShowPromptLibrary);
        _permissionManagerBindingId = _keyboardHook.Register(_appSettings.PermissionManagerHotkey, ShowPermissionManager);
        _claudeConfigBindingId = _keyboardHook.Register(_appSettings.ClaudeConfigHotkey, ShowClaudeConfig);
        _agentsSkillsBindingId = _keyboardHook.Register(_appSettings.AgentsSkillsHotkey, ShowAgentsSkills);
    }

    private void UnregisterAllHotkeys()
    {
        UnregisterGlobalHotkey();
        _keyboardHook?.Unregister(_sessionBrowserBindingId);
        _keyboardHook?.Unregister(_promptLibraryBindingId);
        _keyboardHook?.Unregister(_permissionManagerBindingId);
        _keyboardHook?.Unregister(_claudeConfigBindingId);
        _keyboardHook?.Unregister(_agentsSkillsBindingId);
        _sessionBrowserBindingId = -1;
        _promptLibraryBindingId = -1;
        _permissionManagerBindingId = -1;
        _claudeConfigBindingId = -1;
        _agentsSkillsBindingId = -1;
    }

    private void ToggleVisibility() => ToggleWidget();

    private void TxtTitle_RightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var mode in Enum.GetValues<WidgetViewMode>())
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = mode.ToString(),
                IsChecked = _appSettings.WidgetViewMode == mode
            };
            var m = mode;
            item.Click += (_, _) =>
            {
                _appSettings.WidgetViewMode = m;
                SettingsService.Save(_appSettings);
                ApplyViewMode();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ApplyViewMode()
    {
        var mode = _appSettings.WidgetViewMode;
        var full = mode == WidgetViewMode.Full;
        var compact = mode == WidgetViewMode.Compact;
        var mini = mode == WidgetViewMode.Mini;

        // Session section: Full = full detail, Compact = bars only, Mini = hidden
        panelSession.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        txtSessionReset.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

        // Separator + Weekly: Full/Compact = visible, Mini = hidden
        separatorMain.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        panelWeekly.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        txtWeeklyReset.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

        // Opus: only in Full
        if (panelOpus.Visibility == Visibility.Visible && !full)
        {
            panelOpus.Visibility = Visibility.Collapsed;
        }

        // Tokens section: Full only
        separatorTokens.Visibility = full && separatorTokens.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        panelTokens.Visibility = full && panelTokens.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        // Mini stats line in header
        if (mini)
        {
            UpdateMiniStats();
            txtMiniStats.Visibility = Visibility.Visible;
            Width = 210;
        }
        else
        {
            txtMiniStats.Visibility = Visibility.Collapsed;
            Width = compact ? 200 : 280;
        }

        // Reconcile data-driven panels (Opus, tokens) so switching back to Full
        // restores them correctly without waiting for the next timer refresh.
        var data = _usageService.CachedData;
        if (data != null && full)
        {
            panelOpus.Visibility = data.SevenDayOpus != null ? Visibility.Visible : Visibility.Collapsed;
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, AdjustAfterContentChange);
    }

    private void UpdateMiniStats()
    {
        var data = _usageService.CachedData;
        if (data == null)
        {
            txtMiniStats.Text = "--%  ·  --%";
            return;
        }

        var session = data.FiveHour != null ? $"{Math.Round(data.FiveHour.Utilization)}%" : "--";
        var weekly = data.SevenDay != null ? $"{Math.Round(data.SevenDay.Utilization)}%" : "--";
        txtMiniStats.Text = $"{session}  ·  {weekly}";

        var tooltipLines = new List<string>();
        if (data.FiveHour != null)
        {
            tooltipLines.Add($"Session (5h): {Math.Round(data.FiveHour.Utilization)}%");
        }
        if (data.SevenDay != null)
        {
            tooltipLines.Add($"Weekly: {Math.Round(data.SevenDay.Utilization)}%");
        }
        if (data.SevenDayOpus != null)
        {
            tooltipLines.Add($"Opus: {Math.Round(data.SevenDayOpus.Utilization)}%");
        }
        tooltipLines.Add("");
        tooltipLines.Add("Right-click title to change view mode.");
        txtMiniStats.ToolTip = string.Join("\n", tooltipLines);
    }

    private void RegisterGlobalHotkey()
    {
        _widgetBindingId = _keyboardHook!.Register(_appSettings.WidgetToggleHotkey, ToggleVisibility);
        _hotkeyRegistered = _widgetBindingId >= 0;
    }

    private void UnregisterGlobalHotkey()
    {
        if (_hotkeyRegistered)
        {
            _keyboardHook?.Unregister(_widgetBindingId);
            _widgetBindingId = -1;
            _hotkeyRegistered = false;
        }
    }

    private const string HotkeyValueName = "ClaudeSessionsSidekick_Hotkey";

    private static bool IsHotkeyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
        // Default to enabled for new users (no registry value = first run)
        return key?.GetValue(HotkeyValueName) is not int value || value == 1;
    }

    private void SetHotkeyEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
        if (key == null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(HotkeyValueName, 1, RegistryValueKind.DWord);
            RegisterAllHotkeys();
            RegisterQuickLaunchHotkeys();
        }
        else
        {
            key.DeleteValue(HotkeyValueName, false);
            UnregisterAllHotkeys();
            UnregisterQuickLaunchHotkeys();
        }

        RebuildTrayMenu();
    }

    private void RegisterQuickLaunchHotkeys()
    {
        foreach (var entry in _appSettings.QuickLaunchEntries)
        {
            if (string.IsNullOrEmpty(entry.Hotkey))
            {
                continue;
            }

            var capturedEntry = entry;
            var id = _keyboardHook!.Register(entry.Hotkey, () => ClaudeLauncherService.Launch(capturedEntry));
            if (id >= 0)
            {
                _quickLaunchHotkeys[id] = entry;
            }
        }
    }

    private void UnregisterQuickLaunchHotkeys()
    {
        foreach (var id in _quickLaunchHotkeys.Keys)
        {
            _keyboardHook?.Unregister(id);
        }
        _quickLaunchHotkeys.Clear();
    }

    private void ApplyLauncherSettings()
    {
        ClaudeLauncherService.PreferredShell = _appSettings.PreferredShell;
        ClaudeLauncherService.ClaudeExePath = _appSettings.ClaudeExePath;
        ClaudeConfigService.ClaudeHomeDir = _appSettings.ClaudeHomeDir ?? "";
    }

    private void ReloadSettings()
    {
        UnregisterQuickLaunchHotkeys();
        _appSettings = SettingsService.Load();
        ApplyLauncherSettings();
        RegisterQuickLaunchHotkeys();
        RebuildTrayMenu();
    }

    private bool _userPositioned;
    private double _anchorBottom; // distance from bottom of work area to bottom of widget
    private double _anchorRight;  // distance from right of work area to right of widget

    private void PositionBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 8;
        Top = workArea.Bottom - ActualHeight - 8;
        _anchorBottom = 8;
        _anchorRight = 8;
    }

    /// <summary>
    /// After content changes the widget height, keep it anchored to its current
    /// position instead of snapping back to the default corner.
    /// </summary>
    private void AdjustAfterContentChange()
    {
        if (!_userPositioned)
        {
            PositionBottomRight();
            return;
        }

        // Keep the bottom-right corner anchored where the user left it
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - _anchorRight;
        Top = workArea.Bottom - ActualHeight - _anchorBottom;
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Claude Sessions Sidekick",
            Visible = true
        };

        // Try to load embedded icon, fall back to default
        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/claude.ico");
            var iconStream = Application.GetResourceStream(iconUri)?.Stream;
            _trayIcon.Icon = iconStream != null
                ? new System.Drawing.Icon(iconStream)
                : System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        RebuildTrayMenu();
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                ToggleWidget();
            }
        };
        _trayIcon.BalloonTipClicked += OnBalloonClick;
    }

    private void RebuildTrayMenu()
    {
        if (_trayIcon == null)
        {
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer()
        };
        var showItem = new System.Windows.Forms.ToolStripMenuItem("Show/Hide", null, (_, _) => ToggleWidget());
        if (IsHotkeyEnabled()) showItem.ShortcutKeyDisplayString = _appSettings.WidgetToggleHotkey;
        menu.Items.Add(showItem);

        var viewMenu = new System.Windows.Forms.ToolStripMenuItem("View Mode");
        foreach (var mode in Enum.GetValues<WidgetViewMode>())
        {
            var m = mode;
            var item = new System.Windows.Forms.ToolStripMenuItem(mode.ToString())
            {
                Checked = _appSettings.WidgetViewMode == mode
            };
            item.Click += (_, _) =>
            {
                _appSettings.WidgetViewMode = m;
                SettingsService.Save(_appSettings);
                ApplyViewMode();
                RebuildTrayMenu();
            };
            viewMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(viewMenu);
        menu.Items.Add("-");

        // Quick Launch + Prompts (action submenus at the top)
        if (_appSettings.QuickLaunchEntries.Count > 0)
        {
            var launchMenu = new System.Windows.Forms.ToolStripMenuItem("Quick Launch");
            foreach (var entry in _appSettings.QuickLaunchEntries)
            {
                var capturedEntry = entry;
                var shortcutText = entry.Hotkey != null ? $"  ({entry.Hotkey})" : "";
                launchMenu.DropDownItems.Add(
                    $"{entry.Name}{shortcutText}", null,
                    (_, _) => ClaudeLauncherService.Launch(capturedEntry));
            }
            menu.Items.Add(launchMenu);
        }

        var allPrompts = PromptService.Load();
        if (allPrompts.Count > 0)
        {
            const int maxInMenu = 10;
            var promptMenu = new System.Windows.Forms.ToolStripMenuItem("Prompts");
            foreach (var prompt in allPrompts.Take(maxInMenu))
            {
                var capturedPrompt = prompt;
                promptMenu.DropDownItems.Add(
                    $"{prompt.Name} ({prompt.Category})", null,
                    (_, _) => ShowPromptRunner(capturedPrompt));
            }
            if (allPrompts.Count > maxInMenu)
            {
                promptMenu.DropDownItems.Add($"... and {allPrompts.Count - maxInMenu} more");
            }
            promptMenu.DropDownItems.Add("-");
            var manageItem = new System.Windows.Forms.ToolStripMenuItem("Manage Prompts...", null, (_, _) => ShowPromptLibrary());
            if (IsHotkeyEnabled()) manageItem.ShortcutKeyDisplayString = _appSettings.PromptLibraryHotkey;
            promptMenu.DropDownItems.Add(manageItem);
            menu.Items.Add(promptMenu);
        }
        else
        {
            var promptItem = new System.Windows.Forms.ToolStripMenuItem("Prompt Library...", null, (_, _) => ShowPromptLibrary());
            if (IsHotkeyEnabled()) promptItem.ShortcutKeyDisplayString = _appSettings.PromptLibraryHotkey;
            menu.Items.Add(promptItem);
        }

        // Browse Sessions + Tools
        menu.Items.Add("-");
        var browseItem = new System.Windows.Forms.ToolStripMenuItem("Browse Sessions...", null, (_, _) => ShowSessionBrowser());
        if (IsHotkeyEnabled()) browseItem.ShortcutKeyDisplayString = _appSettings.SessionBrowserHotkey;
        menu.Items.Add(browseItem);

        var toolsMenu = new System.Windows.Forms.ToolStripMenuItem("Tools");
        var permItem = new System.Windows.Forms.ToolStripMenuItem("Permissions...", null, (_, _) => ShowPermissionManager());
        if (IsHotkeyEnabled()) permItem.ShortcutKeyDisplayString = _appSettings.PermissionManagerHotkey;
        toolsMenu.DropDownItems.Add(permItem);
        var memItem = new System.Windows.Forms.ToolStripMenuItem("Memory Manager...", null, (_, _) => ShowClaudeConfig());
        if (IsHotkeyEnabled()) memItem.ShortcutKeyDisplayString = _appSettings.ClaudeConfigHotkey;
        toolsMenu.DropDownItems.Add(memItem);
        var agentsItem = new System.Windows.Forms.ToolStripMenuItem("Agents && Skills...", null, (_, _) => ShowAgentsSkills());
        if (IsHotkeyEnabled()) agentsItem.ShortcutKeyDisplayString = _appSettings.AgentsSkillsHotkey;
        toolsMenu.DropDownItems.Add(agentsItem);
        menu.Items.Add(toolsMenu);

        // Settings section
        menu.Items.Add("-");
        var startupItem = new System.Windows.Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabled()
        };
        startupItem.CheckedChanged += (_, _) => SetStartupEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);
        _hotkeyMenuItem = new System.Windows.Forms.ToolStripMenuItem("Enable App Shortcuts")
        {
            CheckOnClick = true,
            Checked = IsHotkeyEnabled()
        };
        _hotkeyMenuItem.CheckedChanged += (_, _) => SetHotkeyEnabled(_hotkeyMenuItem.Checked);
        menu.Items.Add(_hotkeyMenuItem);
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add("Check for updates...", null, async (_, _) => await CheckForUpdatesInteractiveAsync());
        menu.Items.Add("About...", null, (_, _) => ShowAbout());

        // Diagnostic helpers - useful for the user and for bug reports from beta testers
        var debugMenu = new System.Windows.Forms.ToolStripMenuItem("Debug");
        debugMenu.DropDownItems.Add("Open log folder", null, (_, _) => OpenLogFolder());
        debugMenu.DropDownItems.Add("Clear cached usage data", null, async (_, _) =>
        {
            _usageService.ClearCache();
            await SafeRefreshAsync(manualRefresh: true);
        });
        debugMenu.DropDownItems.Add("Dump state to log", null, (_, _) => DumpStateToLog());
        menu.Items.Add(debugMenu);
        menu.Items.Add("-");
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
    }

    private static void BringToForeground(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();

        // Global hotkeys fire from a background context — Windows won't grant foreground
        // to a non-foreground process unless we call SetForegroundWindow explicitly.
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }
    }

    private void ShowSessionBrowser()
    {
        if (_sessionBrowserWindow is { IsLoaded: true })
        {
            BringToForeground(_sessionBrowserWindow);
            return;
        }

        _sessionBrowserWindow = new SessionBrowserWindow(_appSettings.CompactAggressiveness);
        _sessionBrowserWindow.Closed += (_, _) => _sessionBrowserWindow = null;
        _sessionBrowserWindow.Show();
        BringToForeground(_sessionBrowserWindow);
    }

    private void OpenSettings()
    {
        // Temporarily unregister so bindings can be updated with new settings
        UnregisterAllHotkeys();
        UnregisterQuickLaunchHotkeys();

        var settingsWindow = new SettingsWindow(_appSettings);
        if (settingsWindow.ShowDialog() == true && settingsWindow.SettingsChanged)
        {
            _appSettings = settingsWindow.GetUpdatedSettings();
            ApplyLauncherSettings();
            SettingsService.Save(_appSettings);
            UpdateTokenUI();
        }

        if (IsHotkeyEnabled())
        {
            RegisterAllHotkeys();
            RegisterQuickLaunchHotkeys();
        }
        RebuildTrayMenu();
    }

    private void ShowPromptLibrary()
    {
        var window = new PromptLibraryWindow(_appSettings.QuickLaunchEntries);
        window.Closed += (_, _) => RebuildTrayMenu();
        window.Show();
    }

    private void ShowPermissionManager()
    {
        if (_permissionManagerWindow is { IsLoaded: true })
        {
            BringToForeground(_permissionManagerWindow);
            return;
        }

        _permissionManagerWindow = new PermissionManagerWindow(_appSettings.QuickLaunchEntries);
        _permissionManagerWindow.Closed += (_, _) => _permissionManagerWindow = null;
        _permissionManagerWindow.Show();
        BringToForeground(_permissionManagerWindow);
    }

    private void ShowClaudeConfig()
    {
        if (_claudeConfigWindow is { IsLoaded: true })
        {
            BringToForeground(_claudeConfigWindow);
            return;
        }

        _claudeConfigWindow = new ClaudeConfigWindow(_appSettings.QuickLaunchEntries);
        _claudeConfigWindow.Closed += (_, _) => _claudeConfigWindow = null;
        _claudeConfigWindow.Show();
        BringToForeground(_claudeConfigWindow);
    }

    private void ShowAgentsSkills()
    {
        if (_agentsSkillsWindow is { IsLoaded: true })
        {
            BringToForeground(_agentsSkillsWindow);
            return;
        }

        _agentsSkillsWindow = new AgentsSkillsWindow(_appSettings.QuickLaunchEntries);
        _agentsSkillsWindow.Closed += (_, _) => _agentsSkillsWindow = null;
        _agentsSkillsWindow.Show();
        BringToForeground(_agentsSkillsWindow);
    }

    private void ShowPromptRunner(Models.PromptEntry prompt)
    {
        if (_appSettings.QuickLaunchEntries.Count == 0)
        {
            ShowPromptLibrary();
            return;
        }

        // If only one project, run directly
        if (_appSettings.QuickLaunchEntries.Count == 1)
        {
            ClaudeLauncherService.LaunchWithPrompt(
                _appSettings.QuickLaunchEntries[0].FolderPath, prompt.Prompt);
            return;
        }

        // Multiple projects - open library with the prompt pre-selected
        var window = new PromptLibraryWindow(_appSettings.QuickLaunchEntries, prompt.Id);
        window.Closed += (_, _) => RebuildTrayMenu();
        window.Show();
    }

    private void ShowAbout()
    {
        var about = new AboutWindow();
        about.ShowDialog();
    }

    private static void OpenLogFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppLogger.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppLogger.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open log folder", ex);
        }
    }

    private void DumpStateToLog()
    {
        try
        {
            var aggregated = _sessionWatcher.GetAggregated();
            var runningClaudes = ClaudeProcessService.GetRunningClaudeCodeCount();

            var dump = new System.Text.StringBuilder();
            dump.AppendLine("=== Widget state dump ===");
            dump.AppendLine($"Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            dump.AppendLine($"Settings path: {SettingsService.SettingsPath}");
            dump.AppendLine();
            dump.AppendLine("Usage service:");
            dump.AppendLine(_usageService.GetStateSnapshot());
            dump.AppendLine();
            dump.AppendLine("Session watcher:");
            dump.AppendLine($"  ActiveSessions: {aggregated.ActiveSessionCount}");
            dump.AppendLine($"  RunningClaudeCodeProcesses: {runningClaudes}");
            foreach (var s in aggregated.Sessions)
            {
                dump.AppendLine($"    - {s.ProjectName} ({s.GitBranch ?? "no branch"}) turns={s.TurnCount} topic={s.Topic}");
                // Model + context detection — load-bearing for diagnosing
                // "widget says 200k limit, my actual context is 1M" reports.
                dump.AppendLine($"      model={s.Model ?? "(unknown)"} cwSize={s.ContextWindowSize:N0} cwSource=\"{s.ContextWindowSource}\" shorthand={s.ConfiguredModelIsShorthand} lastTurnCtx={s.LastTurnContextSize:N0} maxObserved={s.MaxObservedContext:N0} autoCompact={(s.AutoCompactThreshold?.ToString("N0") ?? "(none)")}");
                dump.AppendLine($"      cwd={s.Cwd ?? "(none)"}");
            }
            dump.AppendLine();
            // Per-project model resolution: dump every settings file we
            // consult and what we found in each. Lets us reproduce the
            // ContextWindowSize decision tree from the log alone.
            dump.AppendLine("Configured model sources:");
            var distinctCwds = aggregated.Sessions
                .Select(s => s.Cwd)
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Always include the user-level resolution (no project root).
            DumpModelSources(dump, projectRoot: null, label: "(user-level only)");
            foreach (var cwd in distinctCwds)
            {
                DumpModelSources(dump, projectRoot: cwd, label: cwd!);
            }
            dump.AppendLine();
            dump.AppendLine("Settings:");
            dump.AppendLine($"  WidgetToggleHotkey: {_appSettings.WidgetToggleHotkey}");
            dump.AppendLine($"  SessionBrowserHotkey: {_appSettings.SessionBrowserHotkey}");
            dump.AppendLine($"  PromptLibraryHotkey: {_appSettings.PromptLibraryHotkey}");
            dump.AppendLine($"  PermissionManagerHotkey: {_appSettings.PermissionManagerHotkey}");
            dump.AppendLine($"  ClaudeConfigHotkey: {_appSettings.ClaudeConfigHotkey}");
            dump.AppendLine($"  AgentsSkillsHotkey: {_appSettings.AgentsSkillsHotkey}");
            dump.AppendLine($"  CompactAggressiveness: {_appSettings.CompactAggressiveness}");
            dump.AppendLine($"  EnableCompactNotifications: {_appSettings.EnableCompactNotifications}");
            dump.AppendLine($"  QuickLaunchEntries: {_appSettings.QuickLaunchEntries.Count}");
            dump.AppendLine("=== end dump ===");

            AppLogger.Info(dump.ToString());
            _trayIcon?.ShowBalloonTip(3000, "State dumped",
                "Current widget state written to app.log", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLogger.Error("DumpStateToLog failed", ex);
        }
    }

    private static void DumpModelSources(System.Text.StringBuilder dump, string? projectRoot, string label)
    {
        dump.AppendLine($"  Project: {label}");
        var resolved = ClaudeConfigService.GetConfiguredModel(projectRoot);
        var isReduced = ClaudeConfigService.IsReducedContextModel(resolved);
        dump.AppendLine($"    Resolved: {resolved ?? "(none — Claude default)"} reducedContext={isReduced}");
        foreach (var (path, value) in ClaudeConfigService.GetAllConfiguredModelSources(projectRoot))
        {
            dump.AppendLine($"    - {value,-32} {path}");
        }
    }

    private void ToggleWidget()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            AdjustAfterContentChange();
        }
    }

    private void ShowWidget()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        AdjustAfterContentChange();
    }

    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ClaudeSessionsSidekick";

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, false);
        return key?.GetValue(StartupValueName) != null;
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, true);
        if (key == null)
        {
            return;
        }

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? "";
            key.SetValue(StartupValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(StartupValueName, false);
        }
    }

    private void ExitApp()
    {
        AppLogger.Info("Widget exiting");
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        _fastRetryTimer?.Stop();
        _sessionWatcher.Dispose();
        _permissionWatcher.Dispose();
        _trayIcon?.Dispose();
        _usageService.Dispose();
        Application.Current.Shutdown();
    }

    private async Task SafeRefreshAsync(bool forceRefresh = false, bool manualRefresh = false)
    {
        try
        {
            var data = await _usageService.FetchUsageAsync(forceRefresh || manualRefresh, manualRefresh);
            UpdateUI(data);

            // If we had an error and no cached data, schedule faster retries
            if (data == null && _usageService.LastError != null)
            {
                ScheduleFastRetry();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Refresh failed: {ex.Message}");
            AppLogger.Error("SafeRefreshAsync failed", ex);
            txtSessionPercent.Text = "Refresh error";
        }
    }

    private DispatcherTimer? _fastRetryTimer;
    private int _fastRetryAttempt;

    private void ScheduleFastRetry()
    {
        if (_fastRetryTimer != null)
        {
            return; // Already scheduled
        }

        _fastRetryAttempt = 0;
        _fastRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _fastRetryTimer.Tick += async (_, _) =>
        {
            try
            {
                // Don't burn fast-retry attempts while the API is in backoff -
                // the call would just return cached data without hitting the network.
                if (_usageService.IsBackedOff)
                {
                    return;
                }

                _fastRetryAttempt++;
                AppLogger.Info($"Fast retry attempt {_fastRetryAttempt}");
                var data = await _usageService.FetchUsageAsync(forceRefresh: true);
                UpdateUI(data);

                if (data != null || _fastRetryAttempt >= 5)
                {
                    _fastRetryTimer?.Stop();
                    _fastRetryTimer = null;
                    if (data != null)
                    {
                        AppLogger.Info("Fast retry succeeded - switching back to normal refresh");
                    }
                    else
                    {
                        AppLogger.Warn("Fast retry gave up after 5 attempts");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Fast retry tick failed", ex);
            }
        };
        _fastRetryTimer.Start();
    }

    private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
        {
            // Reset modifier tracking to prevent stuck keys after lock/unlock
            _keyboardHook?.ResetState();
        }

        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            await Dispatcher.InvokeAsync(async () => await SafeRefreshAsync(forceRefresh: true));
        }
    }

    private void UpdateUI(UsageData? data)
    {
        if (data == null)
        {
            // No data yet (initial failed fetch) - show error in status
            var err = _usageService.LastError ?? "No data";
            txtSessionPercent.Text = err;
            txtSessionReset.Text = "Will retry in 3 min";
            AppLogger.Warn($"UpdateUI called with no data: {err}");
            UpdateApiErrorBanner();
            return;
        }

        UpdateApiErrorBanner();

        // We have data (either fresh or cached from before). If last error is set, show subtle indicator.
        if (_usageService.LastError != null)
        {
            txtSessionReset.ToolTip = $"Last refresh: {_usageService.LastFetchTime.ToLocalTime():HH:mm:ss}\nLast error: {_usageService.LastError}";
        }
        else
        {
            txtSessionReset.ToolTip = $"Last refresh: {_usageService.LastFetchTime.ToLocalTime():HH:mm:ss}";
        }

        // Session (5h)
        if (data.FiveHour != null)
        {
            var pct = Math.Round(data.FiveHour.Utilization);
            txtSessionPercent.Text = $"{pct}%";
            barSession.Value = pct;
            UpdateBarWidth(barSession, pct);
            UpdateResetText(txtSessionReset, data.FiveHour.ResetsAt, useRelative: true);
            UpdateBarColor(barSession, pct);
            UpdatePercentColor(txtSessionPercent, pct);
        }

        // Weekly - All models (7 day)
        if (data.SevenDay != null)
        {
            var pct = Math.Round(data.SevenDay.Utilization);
            txtWeeklyPercent.Text = $"{pct}%";
            barWeekly.Value = pct;
            UpdateBarWidth(barWeekly, pct);
            UpdateResetText(txtWeeklyReset, data.SevenDay.ResetsAt, useRelative: false);
            UpdateBarColor(barWeekly, pct);
            UpdatePercentColor(txtWeeklyPercent, pct);
        }

        // Opus (if present — Max 5x plan)
        if (data.SevenDayOpus != null)
        {
            panelOpus.Visibility = Visibility.Visible;
            var pct = Math.Round(data.SevenDayOpus.Utilization);
            txtOpusPercent.Text = $"{pct}%";
            barOpus.Value = pct;
            UpdateBarWidth(barOpus, pct);
            UpdateResetText(txtOpusReset, data.SevenDayOpus.ResetsAt, useRelative: false);
            UpdateBarColor(barOpus, pct);
            UpdatePercentColor(txtOpusPercent, pct);
        }
        else
        {
            panelOpus.Visibility = Visibility.Collapsed;
        }

        // Update mini stats if in Mini view mode
        if (_appSettings.WidgetViewMode == WidgetViewMode.Mini)
        {
            UpdateMiniStats();
        }

        // Reposition after content change
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AdjustAfterContentChange);
    }

    /// <summary>
    /// Shows/hides a visible error banner at the top of the widget when the API is
    /// in a degraded state (token expired, rate limited, auto-retry stopped).
    /// This replaces the previous tooltip-only approach which was invisible.
    /// </summary>
    private void UpdateApiErrorBanner()
    {
        // No error state - hide banner
        if (_usageService.LastError == null)
        {
            panelApiError.Visibility = Visibility.Collapsed;
            return;
        }

        var lastError = _usageService.LastError;
        string message;
        string? tooltip = null;

        if (_usageService.AutoRetryDisabled)
        {
            // Prefer the tracked error category over text matching, since LastError
            // gets overwritten with a generic "Auto-retry stopped" message.
            var isAuth = _usageService.LastErrorCategory == ApiErrorCategory.Auth
                || IsAuthError(lastError);
            if (isAuth)
            {
                message = "\u26A0 Session inactive \u2014 open Claude Code to refresh";
                tooltip = "No active Claude Code session detected.\n\n" +
                          "The usage token expires after a period of inactivity.\n" +
                          "To fix: open Claude Code in any terminal \u2014 the token\n" +
                          "refreshes automatically when you start a session.\n\n" +
                          "Or click \u21BB (Refresh) to retry with the current token.\n\n" +
                          $"Last successful data: {FormatLastFetchTime()}";
            }
            else
            {
                message = "\u26A0 API unreachable \u2014 click Refresh to retry";
                tooltip = $"The widget couldn't reach the Claude API after {_usageService.ConsecutiveFailures} attempts.\n\n" +
                          "Click \u21BB (Refresh) to try again.\n" +
                          "If it persists, check your internet connection\n" +
                          "or see app.log via tray \u2192 Debug \u2192 Open log folder.\n\n" +
                          $"Last successful data: {FormatLastFetchTime()}";
            }
        }
        else if (_usageService.IsBackedOff)
        {
            var mins = Math.Max(1, (int)Math.Ceiling(_usageService.BackoffRemaining.TotalMinutes));
            if (IsAuthError(lastError))
            {
                message = $"\u26A0 Token expired \u2014 retry in {mins}m";
                tooltip = "Your session token expired after a period of inactivity.\n\n" +
                          "The widget will retry automatically, or you can open\n" +
                          "Claude Code in any terminal to refresh the token instantly.\n\n" +
                          $"Last successful data: {FormatLastFetchTime()}";
            }
            else
            {
                message = $"\u26A0 API throttled \u2014 retry in {mins}m";
                tooltip = "Too many requests to the Claude usage API.\n\n" +
                          "This is just a polling throttle, not your plan quota.\n" +
                          "You can keep using Claude Code normally.\n" +
                          $"Will auto-refresh in {mins} minute(s).\n\n" +
                          $"Last successful data: {FormatLastFetchTime()}";
            }
        }
        else
        {
            // Transient error that will auto-resolve
            message = $"\u26A0 {lastError}";
        }

        txtApiError.Text = message;
        panelApiError.ToolTip = tooltip;
        panelApiError.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AdjustAfterContentChange);
    }

    private string FormatLastFetchTime()
    {
        return _usageService.LastFetchTime == DateTime.MinValue
            ? "never"
            : _usageService.LastFetchTime.ToLocalTime().ToString("HH:mm");
    }

    /// <summary>
    /// Classifies the last API error into a category for consistent messaging
    /// across the error banner and backoff tooltip.
    /// </summary>
    private static bool IsAuthError(string error)
    {
        return error.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("session", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("401", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("auth", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRateLimitError(string error)
    {
        return error.Contains("throttled", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("429", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateBarWidth(System.Windows.Controls.ProgressBar bar, double percent)
    {
        var maxWidth = bar.ActualWidth > 0 ? bar.ActualWidth : 250;
        bar.Tag = maxWidth * Math.Min(percent, 100) / 100.0;
    }

    private static readonly SolidColorBrush ColorNormal = new(Color.FromRgb(0x5B, 0x8D, 0xEF));
    private static readonly SolidColorBrush ColorWarning = new(Color.FromRgb(0xE8, 0xA0, 0x30));
    private static readonly SolidColorBrush ColorCritical = new(Color.FromRgb(0xE0, 0x50, 0x50));
    private static readonly SolidColorBrush ColorPercentNormal = new(Color.FromRgb(0xC0, 0xC0, 0xD0));
    private static readonly SolidColorBrush ColorPercentWarning = new(Color.FromRgb(0xF0, 0xB0, 0x40));
    private static readonly SolidColorBrush ColorPercentCritical = new(Color.FromRgb(0xF0, 0x60, 0x60));

    private static void UpdateBarColor(System.Windows.Controls.ProgressBar bar, double percent)
    {
        bar.Foreground = percent switch
        {
            >= 90 => ColorCritical,
            >= 80 => ColorWarning,
            _ => ColorNormal
        };
    }

    private static void UpdatePercentColor(System.Windows.Controls.TextBlock text, double percent)
    {
        text.Foreground = percent switch
        {
            >= 90 => ColorPercentCritical,
            >= 80 => ColorPercentWarning,
            _ => ColorPercentNormal
        };
    }

    private static void UpdateResetText(System.Windows.Controls.TextBlock textBlock, DateTimeOffset resetTime, bool useRelative)
    {
        // MinValue is the sentinel from TolerantDateTimeOffsetConverter for missing
        // or zero timestamps - don't render it as "reset passed 2000 years ago".
        if (resetTime == DateTimeOffset.MinValue)
        {
            textBlock.Text = "No activity in current window";
            textBlock.ToolTip = "The 5-hour usage window starts when you send your first message.\n" +
                                "Until then the API has no session data to report.\n\n" +
                                "Weekly usage (below) is always available.\n" +
                                "Start using Claude Code and this will update automatically.";
            return;
        }
        textBlock.ToolTip = null;
        UpdateResetTextImpl(textBlock, resetTime, useRelative);
    }

    private static void UpdateResetTextImpl(System.Windows.Controls.TextBlock textBlock, DateTimeOffset resetTime, bool useRelative)
    {
        if (useRelative)
        {
            var remaining = resetTime - DateTimeOffset.UtcNow;
            var localTime = resetTime.ToLocalTime().ToString("HH:mm");
            if (remaining.TotalSeconds <= 0)
            {
                // Reset time passed but server hasn't returned new data yet
                var overdue = -remaining;
                if (overdue.TotalMinutes >= 1)
                {
                    textBlock.Text = $"Reset overdue ({(int)overdue.TotalMinutes}m) - waiting for server...";
                }
                else
                {
                    textBlock.Text = "Resetting now...";
                }
            }
            else if (remaining.TotalHours >= 1)
            {
                textBlock.Text = $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes:D2}m (at {localTime})";
            }
            else
            {
                textBlock.Text = $"Resets in {remaining.Minutes}m (at {localTime})";
            }
        }
        else
        {
            var local = resetTime.ToLocalTime();
            textBlock.Text = $"Resets {local:ddd HH:mm}";
        }
    }

    private DateTime _lastResetRefreshAttempt = DateTime.MinValue;

    private async void UpdateCountdowns()
    {
        var data = _usageService.CachedData;
        if (data == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // Exclude MinValue-sentinel windows - those mean "unknown reset time"
        // (e.g. API returned 0/null) and would otherwise trigger a force-refresh loop.
        bool anyResetPassed =
            (data.FiveHour != null && data.FiveHour.ResetsAt > DateTimeOffset.MinValue && data.FiveHour.ResetsAt <= now) ||
            (data.SevenDay != null && data.SevenDay.ResetsAt > DateTimeOffset.MinValue && data.SevenDay.ResetsAt <= now) ||
            (data.SevenDayOpus != null && data.SevenDayOpus.ResetsAt > DateTimeOffset.MinValue && data.SevenDayOpus.ResetsAt <= now);

        // Always update weekly/opus countdowns first - they shouldn't go stale
        // just because the 5-hour reset passed and we're waiting on the server.
        if (data.FiveHour != null)
        {
            UpdateResetText(txtSessionReset, data.FiveHour.ResetsAt, useRelative: true);
        }

        if (data.SevenDay != null)
        {
            UpdateResetText(txtWeeklyReset, data.SevenDay.ResetsAt, useRelative: false);
        }

        if (data.SevenDayOpus != null)
        {
            UpdateResetText(txtOpusReset, data.SevenDayOpus.ResetsAt, useRelative: false);
        }

        // Update the error banner (countdown timer on backoff, clear when resolved)
        UpdateApiErrorBanner();

        // If the API is in backoff, surface it in the reset text too
        if (_usageService.IsBackedOff)
        {
            var mins = Math.Max(1, (int)Math.Ceiling(_usageService.BackoffRemaining.TotalMinutes));
            txtSessionReset.Text = $"Data may be outdated (retry in {mins}m)";
            txtSessionReset.ToolTip = BuildBackoffTooltip(mins);
        }
        else if (_usageService.AutoRetryDisabled)
        {
            txtSessionReset.Text = "Data may be outdated \u2014 click \u21BB to refresh";
            txtSessionReset.ToolTip = BuildBackoffTooltip(0);
        }
        else
        {
            // Clear any leftover tooltip from a previous backoff
            txtSessionReset.ToolTip = null;
        }

        if (anyResetPassed && !_usageService.IsBackedOff)
        {
            // Throttle reset refresh attempts - server may take a few minutes to update
            // Avoid hammering the API every 30s when reset just happened
            var sinceLastAttempt = DateTime.UtcNow - _lastResetRefreshAttempt;
            if (sinceLastAttempt < TimeSpan.FromSeconds(60))
            {
                return;
            }

            _lastResetRefreshAttempt = DateTime.UtcNow;
            AppLogger.Info($"Reset passed - forcing refresh (attempt at {DateTime.Now:HH:mm:ss})");
            await SafeRefreshAsync(forceRefresh: true);
        }
    }

    private string BuildBackoffTooltip(int retryMins)
    {
        var lastFetchLocal = _usageService.LastFetchTime == DateTime.MinValue
            ? "never"
            : _usageService.LastFetchTime.ToLocalTime().ToString("HH:mm");
        var lastError = _usageService.LastError ?? "";

        if (_usageService.AutoRetryDisabled)
        {
            var isAuthIssue = IsAuthError(lastError);
            var hint = isAuthIssue
                ? "This usually happens after a few hours of inactivity. " +
                  "Just start using Claude Code again - the token will refresh automatically " +
                  "and the widget will update within a minute."
                : "Click the refresh button (top right of the widget) to try again manually. " +
                  "If it still fails, check app.log via tray > Debug > Open log folder.";

            return
                $"Auto-refresh paused ({_usageService.ConsecutiveFailures} failed attempts).\n" +
                $"\n" +
                $"Values shown are from {lastFetchLocal}.\n" +
                $"\n" +
                hint;
        }

        if (IsRateLimitError(lastError))
        {
            return
                $"Claude API asked us to wait before querying again (too many requests to the usage endpoint).\n" +
                $"\n" +
                $"Values shown are from {lastFetchLocal} and will auto-refresh in about {retryMins} minute(s).\n" +
                $"\n" +
                $"This is not your plan quota - just a throttle on how often we can poll for statistics. " +
                $"You can keep using Claude Code normally.";
        }

        if (IsAuthError(lastError))
        {
            return
                $"Your Claude session token has expired after a period of inactivity.\n" +
                $"\n" +
                $"This is normal - tokens expire after a few hours without use.\n" +
                $"Just start using Claude Code and the token will refresh automatically.\n" +
                $"The widget will pick up the new token and update within a minute.\n" +
                $"\n" +
                $"Values shown are from {lastFetchLocal}.";
        }

        if (lastError.Contains("format", StringComparison.OrdinalIgnoreCase))
        {
            return
                $"The Claude usage API returned data in an unexpected format.\n" +
                $"\n" +
                $"Values shown are from {lastFetchLocal}.\n" +
                $"\n" +
                $"Check app.log via tray → Debug → Open log folder and report this to the widget maintainer.";
        }

        return
            $"API temporarily unavailable - {lastError}\n" +
            $"Values shown are from {lastFetchLocal}.\n" +
            $"Will retry in about {retryMins} minute(s).";
    }

    private void UpdateTokenUI()
    {
        var data = _sessionWatcher.GetAggregated();

        if (!_appSettings.ShowActiveSessions || data.ActiveSessionCount == 0)
        {
            separatorTokens.Visibility = Visibility.Collapsed;
            panelTokens.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AdjustAfterContentChange);
            return;
        }

        separatorTokens.Visibility = Visibility.Visible;
        panelTokens.Visibility = Visibility.Visible;

        txtTokenTotals.Text = $"{FormatTokens(data.TotalInputTokens + data.TotalCacheTokens)} in  /  {FormatTokens(data.TotalOutputTokens)} out";
        txtTokenTotals.ToolTip = $"Input: {data.TotalInputTokens:N0}\nCache: {data.TotalCacheTokens:N0}\nOutput: {data.TotalOutputTokens:N0}";

        // Show attention indicator on the collapsed header when any session
        // has a warning/critical compact recommendation.
        var worstLevel = CompactLevel.None;
        foreach (var s in data.Sessions)
        {
            var level = s.GetRecommendation(_appSettings.CompactAggressiveness, _appSettings.CustomCriticalPercent, _appSettings.CustomWarningPercent).Level;
            if (level > worstLevel)
            {
                worstLevel = level;
            }
        }

        var sessionCountText = data.ActiveSessionCount == 1 ? "1 session" : $"{data.ActiveSessionCount} sessions";
        var (summaryIcon, summaryBrush) = worstLevel switch
        {
            CompactLevel.Critical => (" \u26A0", RecommendCriticalBrush),
            CompactLevel.Warning => (" \u26A0", RecommendWarningBrush),
            CompactLevel.Hint => (" \u2139", RecommendHintBrush),
            _ => ("", SummaryDefaultBrush)
        };
        txtTokenSummary.Text = $"{sessionCountText}{summaryIcon}";
        txtTokenSummary.Foreground = summaryBrush;
        txtTokenSummary.ToolTip = worstLevel >= CompactLevel.Warning
            ? "A session needs attention \u2014 click to expand details"
            : null;

        // Only build per-session details when expanded (avoid creating
        // discarded UI elements); TokenHeader_Click rebuilds on expand.
        // Recommendations are always built to trigger toast notifications.
        if (_sessionsExpanded)
        {
            BuildSessionDetails(data.Sessions);
        }
        BuildRecommendations(data.Sessions);

        // Maintain expand/collapse state
        panelSessionExpanded.Visibility = _sessionsExpanded ? Visibility.Visible : Visibility.Collapsed;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AdjustAfterContentChange);
    }

    private static readonly SolidColorBrush SummaryDefaultBrush = new(Color.FromRgb(0x99, 0x9A, 0xAA));
    private static readonly SolidColorBrush RecommendHintBrush = new(Color.FromRgb(0x88, 0x99, 0xAA));
    private static readonly SolidColorBrush RecommendWarningBrush = new(Color.FromRgb(0xE8, 0xA0, 0x30));
    private static readonly SolidColorBrush RecommendCriticalBrush = new(Color.FromRgb(0xE0, 0x50, 0x50));

    private void BuildRecommendations(List<SessionTokenData> sessions)
    {
        panelRecommendations.Children.Clear();

        foreach (var s in sessions)
        {
            var rec = s.GetRecommendation(_appSettings.CompactAggressiveness, _appSettings.CustomCriticalPercent, _appSettings.CustomWarningPercent);
            if (rec.Level == CompactLevel.None)
            {
                continue;
            }

            var contextPct = (int)(100.0 * s.LastTurnContextSize / s.ContextWindowSize);
            var contextStr = FormatTokens(s.LastTurnContextSize);
            var windowStr = FormatTokens(s.ContextWindowSize);
            var projectLabel = s.GitBranch != null ? $"{s.ProjectName} ({s.GitBranch})" : s.ProjectName;
            var topicShort = TruncateTopic(s.Topic, 40);
            var label = $"{projectLabel} - {topicShort}";

            var (brush, icon) = rec.Level switch
            {
                CompactLevel.Critical => (RecommendCriticalBrush, "\u26A0"),
                CompactLevel.Warning => (RecommendWarningBrush, "\u26A0"),
                _ => (RecommendHintBrush, "\u2139")
            };

            var headline = rec.Level switch
            {
                CompactLevel.Critical => "Consider /compact now",
                CompactLevel.Warning => "Good time to /compact",
                _ => "/compact may help"
            };

            var tooltipLines = new List<string>
            {
                $"{label} - {headline}",
                $"Context: {contextStr} / {windowStr} ({contextPct}%)",
                $"Cache hit ratio: {s.LastTurnCacheHitRatio:P0}  |  Turns: {s.TurnCount}",
                ""
            };
            foreach (var reason in rec.Reasons)
            {
                tooltipLines.Add($"\u2022 {reason}");
            }
            tooltipLines.Add("");
            tooltipLines.Add("Click to copy /compact to clipboard.");

            var text = new TextBlock
            {
                Text = $"{icon} {label}: {contextStr}/{windowStr} ({contextPct}%) - {headline}",
                Foreground = brush,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = string.Join("\n", tooltipLines)
            };

            text.MouseLeftButtonDown += (_, _) =>
            {
                SafeSetClipboard("/compact");
            };

            panelRecommendations.Children.Add(text);

            // Toast notification for critical/warning (once per session per level)
            if (_appSettings.EnableCompactNotifications && rec.Level >= CompactLevel.Warning)
            {
                var notifyKey = $"{s.SessionId}_{rec.Level}";
                if (_notifiedSessions.Add(notifyKey))
                {
                    ShowCompactToast(label, headline, rec.Level);
                }
            }
        }
    }

    private void ShowCompactToast(string sessionLabel, string headline, CompactLevel level)
    {
        if (_trayIcon == null)
        {
            return;
        }

        var icon = level == CompactLevel.Critical
            ? System.Windows.Forms.ToolTipIcon.Error
            : System.Windows.Forms.ToolTipIcon.Warning;

        var body = level == CompactLevel.Critical
            ? "Context is running low - /compact recommended now"
            : "Consider running /compact to free up context space";

        _trayIcon.ShowBalloonTip(
            5000,
            $"Claude - {headline}",
            $"{sessionLabel}\n{body}",
            icon);
    }

    private PermissionSuggestion? _pendingSuggestion;

    private void ShowPermissionSuggestion(PermissionSuggestion suggestion)
    {
        if (_trayIcon == null)
        {
            return;
        }

        if (_pendingSuggestion != null)
        {
            AppLogger.Info($"PermissionWatcher: previous suggestion '{_pendingSuggestion.OriginalRule}' replaced by new one");
        }
        _pendingSuggestion = suggestion;
        var suggestionsText = string.Join(", ", suggestion.Suggestions);

        _trayIcon.ShowBalloonTip(
            8000,
            "Permission rule can be simplified",
            $"New rule: {suggestion.OriginalRule}\n\u2192 Suggested: {suggestionsText}\nClick to apply.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OnBalloonClick(object? sender, EventArgs e)
    {
        if (_pendingSuggestion == null)
        {
            return;
        }

        var suggestion = _pendingSuggestion;
        _pendingSuggestion = null;

        var suggestionsText = string.Join("\n", suggestion.Suggestions.Select(s => $"  \u2022 {s}"));
        var message = $"Original rule:\n  {suggestion.OriginalRule}\n\n" +
                      $"Replace with:\n{suggestionsText}\n\n" +
                      "This will add the broader rule(s) and remove the specific one.";

        var dialog = new ConfirmDialog("Simplify Permission Rule", message);
        if (dialog.ShowDialog() == true)
        {
            _permissionWatcher.ApplySuggestions(suggestion, removeOriginal: true);
        }
    }

    private void BuildSessionDetails(List<SessionTokenData> sessions)
    {
        panelSessionDetails.Children.Clear();
        foreach (var s in sessions)
        {
            var projectLabel = s.GitBranch != null
                ? $"{s.ProjectName} ({s.GitBranch})"
                : s.ProjectName;
            var label = $"{projectLabel} - {TruncateTopic(s.Topic, 40)}";

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xB0, 0xFF)),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // Helps users notice when a session is about to age out of the
                // 10-min active window — and gives an at-a-glance way to spot
                // a false-positive "active" sticking around after the user
                // actually finished (e.g. if upstream JSONL parsing drifts).
                ToolTip = $"Last activity: {FormatLastActivity(s.LastSeen)}"
            };

            var totalIn = s.InputTokens + s.CacheReadTokens + s.CacheCreationTokens;
            var tokenBlock = new TextBlock
            {
                Text = $"{FormatTokens(totalIn)} in / {FormatTokens(s.OutputTokens)} out",
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x9A, 0xAA)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = $"Input: {s.InputTokens:N0}\nCache read: {s.CacheReadTokens:N0}\nCache creation: {s.CacheCreationTokens:N0}\nOutput: {s.OutputTokens:N0}\nTurns: {s.TurnCount}"
            };

            var contextPct = s.ContextWindowSize > 0
                ? (int)(100.0 * s.LastTurnContextSize / s.ContextWindowSize)
                : 0;

            var detailDock = new DockPanel { Margin = new Thickness(0, 1, 0, 0) };

            var modelBlock = new TextBlock
            {
                Text = FormatModel(s.Model),
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x77)),
                FontSize = 10
            };

            var contextBrush = contextPct switch
            {
                >= 75 => new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50)),
                >= 50 => new SolidColorBrush(Color.FromRgb(0xE8, 0xA0, 0x30)),
                _ => new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x77))
            };

            var contextBlock = new TextBlock
            {
                Text = $"ctx {contextPct}%  •  {s.TurnCount} turns",
                Foreground = contextBrush,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                ToolTip = $"Context: {FormatTokens(s.LastTurnContextSize)} / {FormatTokens(s.ContextWindowSize)}\nWindow: {s.ContextWindowSource}\nCache hit: {s.LastTurnCacheHitRatio:P0}"
            };

            detailDock.Children.Add(modelBlock);
            DockPanel.SetDock(contextBlock, Dock.Right);
            detailDock.Children.Add(contextBlock);

            Grid.SetColumn(nameBlock, 0);
            Grid.SetColumn(tokenBlock, 1);
            grid.Children.Add(nameBlock);
            grid.Children.Add(tokenBlock);

            var stack = new StackPanel();
            stack.Children.Add(grid);
            stack.Children.Add(detailDock);

            panelSessionDetails.Children.Add(stack);
        }
    }

    private static string FormatLastActivity(DateTimeOffset lastSeen)
    {
        var ago = DateTimeOffset.UtcNow - lastSeen;
        if (ago.TotalSeconds < 30)
        {
            return "just now";
        }
        if (ago.TotalMinutes < 1)
        {
            return $"{(int)ago.TotalSeconds}s ago";
        }
        if (ago.TotalHours < 1)
        {
            return $"{(int)ago.TotalMinutes} min ago";
        }
        if (ago.TotalDays < 1)
        {
            return $"{(int)ago.TotalHours}h ago";
        }
        return $"{(int)ago.TotalDays}d ago";
    }

    private void TokenHeader_Click(object sender, MouseButtonEventArgs e)
    {
        _sessionsExpanded = !_sessionsExpanded;
        txtTokenExpander.Text = _sessionsExpanded ? "\u25BC" : "\u25B6";
        panelSessionExpanded.Visibility = _sessionsExpanded ? Visibility.Visible : Visibility.Collapsed;

        if (_sessionsExpanded)
        {
            var data = _sessionWatcher.GetAggregated();
            BuildSessionDetails(data.Sessions);
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, AdjustAfterContentChange);
    }

    private static string FormatTokens(long tokens)
    {
        return tokens switch
        {
            >= 1_000_000 => $"{tokens / 1_000_000.0:F1}M",
            >= 1_000 => $"{tokens / 1_000.0:F1}k",
            _ => tokens.ToString()
        };
    }

    private static string TruncateTopic(string topic, int maxLength)
    {
        if (string.IsNullOrEmpty(topic))
        {
            return topic;
        }
        // Collapse newlines/tabs that come from first-message text so the row stays one line
        topic = topic.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        return topic.Length <= maxLength ? topic : topic[..(maxLength - 1)] + "\u2026";
    }

    private static string FormatModel(string? model)
    {
        if (model == null)
        {
            return "";
        }
        // "claude-opus-4-6" -> "Opus 4.6", "claude-sonnet-4-6" -> "Sonnet 4.6"
        return model
            .Replace("claude-", "")
            .Replace("opus-4-6", "Opus 4.6")
            .Replace("sonnet-4-6", "Sonnet 4.6")
            .Replace("haiku-4-5", "Haiku 4.5");
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Don't start drag if clicking on interactive elements (token header, recommendations, buttons)
        if (e.OriginalSource is DependencyObject source &&
            (IsChildOf(source, panelTokens) || IsChildOf(source, btnRefresh) || IsChildOf(source, btnClose)))
        {
            return;
        }

        try
        {
            DragMove();
            // Remember user-chosen position so content changes don't snap back
            _userPositioned = true;
            var workArea = SystemParameters.WorkArea;
            _anchorRight = workArea.Right - Left - ActualWidth;
            _anchorBottom = workArea.Bottom - Top - ActualHeight;
        }
        catch (InvalidOperationException ex)
        {
            // Mouse may have been released, or wrong button - just log and ignore
            AppLogger.Warn($"DragMove failed: {ex.Message}");
        }
    }

    private static void SafeSetClipboard(string text)
    {
        // Clipboard.SetText can hang if another app holds the clipboard.
        // Retry a few times with delays, then give up.
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Clipboard.SetText attempt {i + 1} failed: {ex.Message}");
                System.Threading.Thread.Sleep(50);
            }
        }
    }

    private static bool IsChildOf(DependencyObject child, DependencyObject parent)
    {
        var current = child;
        while (current != null)
        {
            if (current == parent)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        btnRefresh.IsEnabled = false;
        txtSessionPercent.Text = "Refreshing...";
        await SafeRefreshAsync(manualRefresh: true);
        btnRefresh.IsEnabled = true;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _updateCheckCts.Cancel();
        _updateCheckCts.Dispose();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _refreshTimer.Stop();
        _countdownTimer.Stop();
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        _sessionWatcher.Dispose();
        _permissionWatcher.Dispose();
        _trayIcon?.Dispose();
        _usageService.Dispose();
        base.OnClosed(e);
    }
}
