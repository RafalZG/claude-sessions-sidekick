using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

/// <summary>
/// Lists sessions that were open before a restart (or the last snapshot) and lets the
/// user reopen the ones they want. The actual relaunch is delegated back to the caller
/// so this window stays UI-only.
/// </summary>
public partial class SessionRestoreWindow : Window
{
    private readonly Action<List<OpenSessionRef>> _reopen;
    private readonly ObservableCollection<Row> _rows = [];

    public SessionRestoreWindow(IReadOnlyList<OpenSessionRef> sessions, Action<List<OpenSessionRef>> reopen, bool afterRestart = false)
    {
        InitializeComponent();
        _reopen = reopen;

        foreach (var s in sessions.OrderByDescending(s => s.LastSeenUtc))
        {
            _rows.Add(new Row(s));
        }
        lstSessions.ItemsSource = _rows;

        txtSummary.Text = afterRestart
            ? (sessions.Count == 1
                ? "This session was open before the restart. Reopen it?"
                : $"These {sessions.Count} sessions were open before the restart. Pick which to reopen.")
            : (sessions.Count == 1
                ? "One Claude Code session looks open right now. Reopen it in a new terminal?"
                : $"{sessions.Count} Claude Code sessions look open right now. Pick which to reopen in new terminals.");
    }

    private void BtnReopen_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Selected).Select(r => r.Source).ToList();
        Close();
        if (chosen.Count > 0)
        {
            _reopen(chosen);
        }
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = true;
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = false;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private sealed class Row : INotifyPropertyChanged
    {
        public Row(OpenSessionRef s)
        {
            Source = s;
            Selected = true;
        }

        public OpenSessionRef Source { get; }
        public string Topic => string.IsNullOrWhiteSpace(Source.Topic) ? Source.SessionId : Source.Topic;
        public string ProjectName => Source.ProjectName;
        public string? FolderPath => Source.FolderPath;
        public string LastSeenDisplay => FormatAgo(DateTimeOffset.UtcNow - Source.LastSeenUtc);

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string FormatAgo(TimeSpan d)
        {
            if (d < TimeSpan.FromMinutes(1)) return "just now";
            if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
            if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours}h ago";
            return $"{(int)d.TotalDays}d ago";
        }
    }
}
