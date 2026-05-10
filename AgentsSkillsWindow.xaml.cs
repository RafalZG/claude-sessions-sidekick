using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

public partial class AgentsSkillsWindow : Window
{
    private readonly List<QuickLaunchEntry> _projects;

    // Agents
    private readonly List<AgentScopeOption> _agentScopes = new();
    private List<MemoryEntry> _agentEntries = new();

    // Plugins
    private List<PluginSource> _pluginSources = new();

    public AgentsSkillsWindow(List<QuickLaunchEntry> quickLaunchProjects)
    {
        InitializeComponent();
        Services.DarkTitleBar.Apply(this);

        _projects = quickLaunchProjects;
        BuildAgentScopeList();
        BuildPluginSourceList();
    }

    // ---- Agents tab ----

    private void BuildAgentScopeList()
    {
        _agentScopes.Clear();

        var globalDir = ClaudeConfigService.GlobalAgentsDir;
        var globalCount = Directory.Exists(globalDir)
            ? Directory.GetFiles(globalDir, "*.md").Length : 0;
        _agentScopes.Add(new AgentScopeOption
        {
            DisplayName = $"User agents  (~/.claude/agents/)  ({globalCount} agents)",
            AgentsDirectory = globalDir,
        });

        foreach (var p in _projects.Where(p => !string.IsNullOrWhiteSpace(p.FolderPath))
                                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var dir = ClaudeConfigService.GetProjectAgentsDir(p.FolderPath);
            var count = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.md").Length : 0;
            _agentScopes.Add(new AgentScopeOption
            {
                DisplayName = $"Project agents: {p.Name}  ({count} agents)",
                AgentsDirectory = dir,
            });
        }

        cmbAgentScope.ItemsSource = _agentScopes;
        if (cmbAgentScope.Items.Count > 0)
        {
            cmbAgentScope.SelectedIndex = 0;
        }
    }

    private void CmbAgentScope_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadAgents();

    private void LoadAgents()
    {
        if (cmbAgentScope.SelectedItem is not AgentScopeOption scope) return;
        txtAgentDirPath.Text = scope.AgentsDirectory;
        _agentEntries = ClaudeConfigService.LoadAgentEntries(scope.AgentsDirectory);
        ApplyAgentFilter();
        ClearAgentPreview();
    }

    private void ApplyAgentFilter()
    {
        var query = (txtAgentSearch?.Text ?? "").Trim();
        lstAgents.ItemsSource = string.IsNullOrEmpty(query)
            ? _agentEntries
            : _agentEntries.Where(a =>
                a.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void TxtAgentSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyAgentFilter();

    private void LstAgents_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (lstAgents.SelectedItem is MemoryEntry agent)
        {
            txtAgentName.Text = agent.Name;
            var meta = $"File: {agent.FileName}    Modified: {agent.LastModifiedDisplay}";
            if (!string.IsNullOrEmpty(agent.Tools)) meta += $"\nTools: {agent.Tools}";
            txtAgentMeta.Text = meta;
            txtAgentDescription.Text = agent.Description;
            txtAgentBody.Text = agent.Body;
            chkAgentPreview.Visibility = Visibility.Visible;
            if (chkAgentPreview.IsChecked == true)
            {
                mdAgentBody.Markdown = agent.Body;
            }
            btnDeleteAgent.IsEnabled = true;
        }
        else { ClearAgentPreview(); }
    }

    private void ClearAgentPreview()
    {
        txtAgentName.Text = "Select an agent to preview";
        txtAgentMeta.Text = "";
        txtAgentDescription.Text = "";
        txtAgentBody.Text = "";
        chkAgentPreview.Visibility = Visibility.Collapsed;
        chkAgentPreview.IsChecked = false;
        mdAgentBody.Visibility = Visibility.Collapsed;
        txtAgentBody.Visibility = Visibility.Visible;
        btnDeleteAgent.IsEnabled = false;
    }

    private void MenuOpenAgentFile_Click(object sender, RoutedEventArgs e)
    {
        if (lstAgents.SelectedItem is not MemoryEntry agent) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = agent.FilePath, UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Warn($"Failed to open {agent.FilePath}: {ex.Message}"); }
    }

    private void BtnDeleteAgent_Click(object sender, RoutedEventArgs e) => DeleteSelectedAgent();
    private void MenuDeleteAgent_Click(object sender, RoutedEventArgs e) => DeleteSelectedAgent();

    private void DeleteSelectedAgent()
    {
        if (lstAgents.SelectedItem is not MemoryEntry agent) return;
        var ok = ConfirmDialog.Show("Delete agent?", $"Delete this agent?\n\n  {agent.Name}\n  ({agent.FileName})\n\nThis cannot be undone.", "Delete", this);
        if (!ok) return;
        try
        {
            if (File.Exists(agent.FilePath)) { File.Delete(agent.FilePath); AppLogger.Info($"Deleted agent {agent.FileName}"); }
            _agentEntries.Remove(agent);
            lstAgents.ItemsSource = _agentEntries.ToList();
            ClearAgentPreview();
        }
        catch (Exception ex) { ConfirmDialog.Show("Delete failed", $"Couldn't delete:\n{ex.Message}", "OK", this); }
    }

    private void BtnNewAgent_Click(object sender, RoutedEventArgs e)
    {
        if (cmbAgentScope.SelectedItem is not AgentScopeOption scope) return;
        const string template = "---\nname: my-new-agent\ndescription: Brief description of what this agent specializes in\n---\n\n# My New Agent\n\nYou are a specialized agent for [domain].\n\n## Your Expertise\n\n- List the areas this agent is an expert in\n\n## Instructions\n\n- How should this agent approach tasks?\n\n## Output Format\n\n- How should the agent format its responses?\n";
        try
        {
            Directory.CreateDirectory(scope.AgentsDirectory);
            var fileName = "my-new-agent.md";
            var filePath = Path.Combine(scope.AgentsDirectory, fileName);
            var counter = 1;
            while (File.Exists(filePath)) { fileName = $"my-new-agent-{counter}.md"; filePath = Path.Combine(scope.AgentsDirectory, fileName); counter++; }
            File.WriteAllText(filePath, template);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = filePath, UseShellExecute = true });
            LoadAgents();
        }
        catch (Exception ex) { ConfirmDialog.Show("Create failed", $"Couldn't create:\n{ex.Message}", "OK", this); }
    }

    private void BtnRefreshAgents_Click(object sender, RoutedEventArgs e) => LoadAgents();

    private void TxtAgentDirPath_Click(object sender, MouseButtonEventArgs e)
    {
        var path = txtAgentDirPath.Text;
        if (string.IsNullOrEmpty(path)) return;
        try { if (!Directory.Exists(path)) Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Warn($"Failed to open {path}: {ex.Message}"); }
    }

    // ---- Plugins & Skills tab ----

    private List<PluginInfo> _allPlugins = new();
    private Dictionary<string, string> _installedMeta = new();
    private List<BlockedPlugin> _blocklist = new();

    private void BuildPluginSourceList()
    {
        _pluginSources = ClaudeConfigService.ListPluginSources(_projects);
        _installedMeta = ClaudeConfigService.LoadInstalledPluginsWithMeta();
        _blocklist = ClaudeConfigService.LoadBlocklist();
        cmbPluginSource.ItemsSource = _pluginSources;
        if (cmbPluginSource.Items.Count > 0) cmbPluginSource.SelectedIndex = 0;

        cmbPluginFilter.Items.Clear();
        cmbPluginFilter.Items.Add("All");
        cmbPluginFilter.Items.Add("Installed");
        cmbPluginFilter.Items.Add("Blocked");
        cmbPluginFilter.Items.Add("Available");
        cmbPluginFilter.SelectedIndex = 0;
    }

    private void CmbPluginSource_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadPluginList();
    private void CmbPluginFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyPluginFilter();

    private void LoadPluginList()
    {
        if (cmbPluginSource.SelectedItem is not PluginSource source) return;
        txtPluginDirPath.Text = source.Path;
        if (source.SourceType == PluginSourceType.Marketplace)
        {
            _allPlugins = ClaudeConfigService.LoadMarketplacePlugins(source.Path, source.MarketplaceName);
            foreach (var p in _allPlugins)
            {
                p.IsInstalled = _installedMeta.ContainsKey(p.PluginId);
                p.InstalledVia = _installedMeta.GetValueOrDefault(p.PluginId, "");
                var blocked = _blocklist.FirstOrDefault(b =>
                    string.Equals(b.PluginId, p.PluginId, StringComparison.OrdinalIgnoreCase));
                p.IsBlocked = blocked != null;
                p.BlockReason = blocked?.Text ?? "";
            }
            ApplyPluginFilter();
        }
        else
        {
            _allPlugins = new List<PluginInfo>();
            lstPlugins.ItemsSource = ClaudeConfigService.LoadProjectSkills(source.Path);
        }
        ClearPluginPreview();
    }

    private void ApplyPluginFilter()
    {
        var filter = cmbPluginFilter?.SelectedItem as string ?? "All";
        IEnumerable<PluginInfo> filtered = filter switch
        {
            "Installed" => _allPlugins.Where(p => p.IsInstalled),
            "Blocked" => _allPlugins.Where(p => p.IsBlocked),
            "Available" => _allPlugins.Where(p => !p.IsInstalled && !p.IsBlocked),
            _ => _allPlugins
        };
        lstPlugins.ItemsSource = filtered.ToList();
    }

    private void LstPlugins_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var source = cmbPluginSource.SelectedItem as PluginSource;
        if (source == null) return;

        if (source.SourceType == PluginSourceType.Marketplace && lstPlugins.SelectedItem is PluginInfo plugin)
        {
            var (readme, items) = ClaudeConfigService.LoadPluginDetails(plugin.Directory);
            txtPluginName.Text = plugin.Name;
            var meta = plugin.Components;
            if (!string.IsNullOrEmpty(plugin.StatusDisplay)) meta = $"{plugin.StatusDisplay}  {meta}";
            if (!string.IsNullOrEmpty(plugin.Author)) meta += $"\nBy: {plugin.Author}";
            if (plugin.IsInstalled && !string.IsNullOrEmpty(plugin.InstalledVia)) meta += $"\n{plugin.InstalledVia}";
            if (plugin.IsBlocked && !string.IsNullOrEmpty(plugin.BlockReason)) meta += $"\nBlocked: {plugin.BlockReason}";
            txtPluginMeta.Text = meta;
            txtPluginDescription.Text = plugin.Description;
            var body = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(readme)) body.AppendLine(readme);
            if (items.Count > 0)
            {
                body.AppendLine("\n--- Contents ---");
                foreach (var item in items)
                {
                    body.AppendLine($"\n[{item.Type}] {item.Name}");
                    if (!string.IsNullOrWhiteSpace(item.Description)) body.AppendLine($"  {item.Description}");
                    body.AppendLine($"  File: {item.FileName}");
                }
            }
            txtPluginBody.Text = body.ToString();
            ShowPluginPreviewControls(body.ToString());
        }
        else if (lstPlugins.SelectedItem is MemoryEntry skill)
        {
            txtPluginName.Text = skill.Name;
            var meta = $"File: {skill.FileName}    Modified: {skill.LastModifiedDisplay}";
            if (!string.IsNullOrEmpty(skill.Tools)) meta += $"\nCompanion files: {skill.Tools}";
            txtPluginMeta.Text = meta;
            txtPluginDescription.Text = skill.Description;
            txtPluginBody.Text = skill.Body;
            ShowPluginPreviewControls(skill.Body);
        }
        else { ClearPluginPreview(); }
    }

    private void ShowPluginPreviewControls(string content)
    {
        chkPluginPreview.Visibility = Visibility.Visible;
        if (chkPluginPreview.IsChecked == true)
        {
            mdPluginBody.Markdown = content;
        }
    }

    private void ClearPluginPreview()
    {
        txtPluginName.Text = "Select a plugin or skill to preview";
        txtPluginMeta.Text = "";
        txtPluginDescription.Text = "";
        txtPluginBody.Text = "";
        chkPluginPreview.Visibility = Visibility.Collapsed;
        chkPluginPreview.IsChecked = false;
        mdPluginBody.Visibility = Visibility.Collapsed;
        txtPluginBody.Visibility = Visibility.Visible;
    }

    private void BtnRefreshPlugins_Click(object sender, RoutedEventArgs e) { BuildPluginSourceList(); LoadPluginList(); }

    private void MenuOpenPluginFolder_Click(object sender, RoutedEventArgs e)
    {
        string? dir = null;
        if (lstPlugins.SelectedItem is PluginInfo p) dir = p.Directory;
        else if (lstPlugins.SelectedItem is MemoryEntry s) dir = Path.GetDirectoryName(s.FilePath);
        if (dir == null || !Directory.Exists(dir)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Warn($"Failed to open {dir}: {ex.Message}"); }
    }

    private void TxtPluginDirPath_Click(object sender, MouseButtonEventArgs e)
    {
        var path = txtPluginDirPath.Text;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Warn($"Failed to open {path}: {ex.Message}"); }
    }

    private void MenuToggleInstall_Click(object sender, RoutedEventArgs e)
    {
        if (lstPlugins.SelectedItem is not PluginInfo plugin) return;

        if (plugin.IsInstalled)
        {
            // Uninstall
            var ok = ConfirmDialog.Show("Uninstall plugin?",
                $"Uninstall {plugin.Name}?\n\nThe plugin will be removed from your installed list.",
                "Uninstall", this);
            if (!ok) return;
            try
            {
                ClaudeConfigService.SetPluginInstalled(plugin.PluginId, false);
                _installedMeta = ClaudeConfigService.LoadInstalledPluginsWithMeta();
                LoadPluginList();
            }
            catch (Exception ex) { ConfirmDialog.Show("Error", $"Failed: {ex.Message}", "OK", this); }
        }
        else if (plugin.IsBlocked)
        {
            // Blocked → offer to unblock + install
            var ok = ConfirmDialog.Show("Plugin is blocked",
                $"{plugin.Name} is currently blocked.\n\nUnblock and install it?",
                "Unblock && Install", this);
            if (!ok) return;
            try
            {
                ClaudeConfigService.SetPluginBlocked(plugin.PluginId, false);
                ClaudeConfigService.SetPluginInstalled(plugin.PluginId, true);
                _installedMeta = ClaudeConfigService.LoadInstalledPluginsWithMeta();
                _blocklist = ClaudeConfigService.LoadBlocklist();
                LoadPluginList();
            }
            catch (Exception ex) { ConfirmDialog.Show("Error", $"Failed: {ex.Message}", "OK", this); }
        }
        else
        {
            // Install
            var ok = ConfirmDialog.Show("Install plugin?",
                $"Install {plugin.Name}?\n\nThe plugin will be added to your installed list.",
                "Install", this);
            if (!ok) return;
            try
            {
                ClaudeConfigService.SetPluginInstalled(plugin.PluginId, true);
                _installedMeta = ClaudeConfigService.LoadInstalledPluginsWithMeta();
                LoadPluginList();
            }
            catch (Exception ex) { ConfirmDialog.Show("Error", $"Failed: {ex.Message}", "OK", this); }
        }
    }

    private void MenuToggleBlock_Click(object sender, RoutedEventArgs e)
    {
        if (lstPlugins.SelectedItem is not PluginInfo plugin) return;

        if (plugin.IsBlocked)
        {
            // Unblock
            var ok = ConfirmDialog.Show("Unblock plugin?",
                $"Unblock {plugin.Name}?\n\nThe plugin will be available again (but not automatically installed).",
                "Unblock", this);
            if (!ok) return;
            try
            {
                ClaudeConfigService.SetPluginBlocked(plugin.PluginId, false);
                _blocklist = ClaudeConfigService.LoadBlocklist();
                LoadPluginList();
            }
            catch (Exception ex) { ConfirmDialog.Show("Error", $"Failed: {ex.Message}", "OK", this); }
        }
        else
        {
            // Block — also uninstall if installed
            var extra = plugin.IsInstalled ? "\n\nThis will also uninstall the plugin." : "";
            var ok = ConfirmDialog.Show("Block plugin?",
                $"Block {plugin.Name}?\n\nBlocked plugins won't be loaded by Claude Code.{extra}",
                "Block", this);
            if (!ok) return;
            try
            {
                if (plugin.IsInstalled)
                {
                    ClaudeConfigService.SetPluginInstalled(plugin.PluginId, false);
                }
                ClaudeConfigService.SetPluginBlocked(plugin.PluginId, true);
                _installedMeta = ClaudeConfigService.LoadInstalledPluginsWithMeta();
                _blocklist = ClaudeConfigService.LoadBlocklist();
                LoadPluginList();
            }
            catch (Exception ex) { ConfirmDialog.Show("Error", $"Failed: {ex.Message}", "OK", this); }
        }
    }

    // ---- Window ----

    private void ChkAgentPreview_Changed(object sender, RoutedEventArgs e)
    {
        var preview = chkAgentPreview.IsChecked == true;
        txtAgentBody.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        mdAgentBody.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
        {
            mdAgentBody.Markdown = txtAgentBody.Text;
            mdAgentBody.SetValue(FlowDocumentScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        }
    }

    private void ChkPluginPreview_Changed(object sender, RoutedEventArgs e)
    {
        var preview = chkPluginPreview.IsChecked == true;
        txtPluginBody.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        mdPluginBody.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
        {
            mdPluginBody.Markdown = txtPluginBody.Text;
            mdPluginBody.SetValue(FlowDocumentScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }

    // Fix mouse wheel scrolling inside read-only TextBoxes nested in ScrollViewers.
    // WPF TextBox eats MouseWheel events even when read-only, preventing parent scroll.
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
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

    private bool _mcpLoaded;
    private List<McpServerEntry> _allMcpServers = new();

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source is TabControl tc && !_mcpLoaded &&
            tc.SelectedItem is TabItem tab && tab.Header?.ToString() == "MCP Servers")
        {
            _mcpLoaded = true;
            RefreshMcpData();
        }
    }

    // ---- MCP Servers tab ----

    private void BtnRefreshMcp_Click(object sender, RoutedEventArgs e)
    {
        _mcpLoaded = true;
        RefreshMcpData();
    }

    private void RefreshMcpData()
    {
        var servers = McpConfigService.ScanAll();
        // Deduplicate — same server may appear in marketplace + cache
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _allMcpServers = new List<McpServerEntry>();
        foreach (var s in servers)
        {
            if (seen.Add(s.Name))
            {
                _allMcpServers.Add(s);
            }
        }

        // Build source dropdown
        var sources = _allMcpServers.Select(s => s.Source).Distinct().ToList();
        sources.Insert(0, "All sources");
        var prevSelection = cmbMcpSource.SelectedItem as string;
        cmbMcpSource.ItemsSource = sources;
        cmbMcpSource.SelectedItem = prevSelection != null && sources.Contains(prevSelection)
            ? prevSelection : "All sources";

        FilterMcpServers();
    }

    private void CmbMcpSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FilterMcpServers();
    }

    private void FilterMcpServers()
    {
        var source = cmbMcpSource.SelectedItem as string;
        var filtered = source == "All sources" || string.IsNullOrEmpty(source)
            ? _allMcpServers
            : _allMcpServers.Where(s => s.Source == source).ToList();

        lstMcpServers.ItemsSource = filtered;

        // Show config file path for selected source
        if (source != "All sources" && filtered.Count > 0)
        {
            txtMcpConfigPath.Text = filtered[0].ConfigFile;
        }
        else
        {
            txtMcpConfigPath.Text = "";
        }

        ClearMcpDetail();
    }

    private void BtnMcpAdd_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new McpEditWindow { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshMcpData();
        }
    }

    private void MenuMcpEdit_Click(object sender, RoutedEventArgs e)
    {
        if (lstMcpServers.SelectedItem is not McpServerEntry server || !server.IsUserEditable)
        {
            return;
        }

        var raw = McpConfigService.LoadServerRaw(server.Name);
        if (raw == null)
        {
            return;
        }

        var dlg = new McpEditWindow(raw) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            RefreshMcpData();
        }
    }

    private void MenuMcpToggle_Click(object sender, RoutedEventArgs e)
    {
        if (lstMcpServers.SelectedItem is not McpServerEntry server || !server.IsUserEditable)
        {
            return;
        }

        McpConfigService.SetServerDisabled(server.Name, !server.IsDisabled);
        RefreshMcpData();
    }

    private void MenuMcpRemove_Click(object sender, RoutedEventArgs e)
    {
        if (lstMcpServers.SelectedItem is not McpServerEntry server || !server.IsUserEditable)
        {
            return;
        }

        if (!ConfirmDialog.Show("Remove MCP Server",
            $"Remove \"{server.Name}\" from configuration?", "Remove", this))
        {
            return;
        }

        McpConfigService.RemoveServer(server.Name);
        RefreshMcpData();
    }

    private void LstMcpServers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (lstMcpServers.SelectedItem is not McpServerEntry server)
        {
            ClearMcpDetail();
            btnMcpEdit.IsEnabled = false;
            btnMcpToggle.IsEnabled = false;
            btnMcpRemove.IsEnabled = false;
            return;
        }

        btnMcpEdit.IsEnabled = server.IsUserEditable;
        btnMcpToggle.IsEnabled = server.IsUserEditable;
        btnMcpRemove.IsEnabled = server.IsUserEditable;
        btnMcpToggle.Content = server.IsDisabled ? "Enable" : "Disable";

        txtMcpName.Text = server.Name;
        txtMcpSource.Text = server.Source;
        txtMcpType.Text = $"Transport: {server.TypeDisplay}";

        if (!string.IsNullOrEmpty(server.Url))
        {
            txtMcpEndpoint.Text = server.Url;
        }
        else if (!string.IsNullOrEmpty(server.Command))
        {
            txtMcpEndpoint.Text = $"{server.Command} {server.Args}".Trim();
        }
        else
        {
            txtMcpEndpoint.Text = "";
        }

        if (server.EnvVars.Count > 0)
        {
            txtMcpEnvHeader.Visibility = Visibility.Visible;
            txtMcpEnv.Visibility = Visibility.Visible;
            txtMcpEnv.Text = string.Join("\n", server.EnvVars.Select(kv => $"  {kv.Key} = {kv.Value}"));
        }
        else
        {
            txtMcpEnvHeader.Visibility = Visibility.Collapsed;
            txtMcpEnv.Visibility = Visibility.Collapsed;
        }

        if (server.Headers.Count > 0)
        {
            txtMcpHeadersHeader.Visibility = Visibility.Visible;
            txtMcpHeaders.Visibility = Visibility.Visible;
            txtMcpHeaders.Text = string.Join("\n", server.Headers.Select(kv => $"  {kv.Key}: {kv.Value}"));
        }
        else
        {
            txtMcpHeadersHeader.Visibility = Visibility.Collapsed;
            txtMcpHeaders.Visibility = Visibility.Collapsed;
        }

        txtMcpConfigFile.Text = $"Config: {server.ConfigFile}";

        var issue = server.HealthIssue;
        if (!string.IsNullOrEmpty(issue))
        {
            txtMcpHealth.Text = issue;
            txtMcpHealth.Visibility = Visibility.Visible;
        }
        else
        {
            txtMcpHealth.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearMcpDetail()
    {
        txtMcpName.Text = "Select an MCP server";
        txtMcpSource.Text = "";
        txtMcpType.Text = "";
        txtMcpEndpoint.Text = "";
        txtMcpEnvHeader.Visibility = Visibility.Collapsed;
        txtMcpEnv.Visibility = Visibility.Collapsed;
        txtMcpHeadersHeader.Visibility = Visibility.Collapsed;
        txtMcpHeaders.Visibility = Visibility.Collapsed;
        txtMcpHealth.Visibility = Visibility.Collapsed;
        txtMcpConfigFile.Text = "";
    }

    private class AgentScopeOption
    {
        public string DisplayName { get; set; } = "";
        public string AgentsDirectory { get; set; } = "";
        public override string ToString() => DisplayName;
    }
}
