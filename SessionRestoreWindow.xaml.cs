using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ClaudeSessionsSidekick.Models;
using ClaudeSessionsSidekick.Services;

namespace ClaudeSessionsSidekick;

/// <summary>
/// Lists sessions that were open before a restart (or the last snapshot) and lets the
/// user reopen the ones they want. A dropdown offers older snapshots from the journal
/// ("what was open on Friday morning"). The actual relaunch is delegated back to the
/// caller so this window stays UI-only.
/// </summary>
public partial class SessionRestoreWindow : Window
{
    private readonly Action<List<OpenSessionRef>> _reopen;
    private readonly ObservableCollection<Row> _rows = [];
    private readonly IReadOnlySet<string> _openNowIds;

    public SessionRestoreWindow(
        IReadOnlyList<OpenSessionRef> sessions
        , Action<List<OpenSessionRef>> reopen
        , bool afterRestart = false
        , IReadOnlyList<OpenSessionSnapshot>? history = null
        , IReadOnlySet<string>? openNowIds = null)
    {
        InitializeComponent();
        _reopen = reopen;
        _openNowIds = openNowIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lstSessions.ItemsSource = _rows;

        var choices = BuildChoices(sessions, afterRestart, history);
        cmbSnapshot.ItemsSource = choices;
        cmbSnapshot.Visibility = choices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (choices.Count > 0)
        {
            cmbSnapshot.SelectedIndex = 0;
        }
    }

    private static List<SnapshotChoice> BuildChoices(
        IReadOnlyList<OpenSessionRef> sessions
        , bool afterRestart
        , IReadOnlyList<OpenSessionSnapshot>? history)
    {
        var choices = new List<SnapshotChoice>();

        if (sessions.Count > 0)
        {
            var currentLabel = afterRestart
                ? $"Before restart  ·  {sessions.Count} session{(sessions.Count == 1 ? "" : "s")}"
                : $"Open now  ·  {sessions.Count} session{(sessions.Count == 1 ? "" : "s")}";
            var currentSummary = afterRestart
                ? (sessions.Count == 1
                    ? "This session was open before the restart. Reopen it?"
                    : $"These {sessions.Count} sessions were open before the restart. Pick which to reopen.")
                : (sessions.Count == 1
                    ? "One Claude Code session looks open right now. Reopen it in a new terminal?"
                    : $"{sessions.Count} Claude Code sessions look open right now. Pick which to reopen in new terminals.");
            var current = new SnapshotChoice
            {
                Label = currentLabel,
                Summary = currentSummary,
                Sessions = sessions,
                IsHistory = false,
            };
            choices.Add(current);
        }

        if (history != null)
        {
            var currentIds = sessions
                .Select(s => s.SessionId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var snapshot in history.OrderByDescending(h => h.CapturedUtc))
            {
                if (snapshot.Sessions.Count == 0)
                {
                    continue;
                }

                // A past moment with exactly the sessions already shown adds nothing.
                if (snapshot.Sessions.Count == currentIds.Count
                    && snapshot.Sessions.All(s => currentIds.Contains(s.SessionId)))
                {
                    continue;
                }

                var when = snapshot.CapturedUtc.ToLocalTime().ToString("ddd dd.MM HH:mm");
                var entry = new SnapshotChoice
                {
                    Label = $"{when}  ·  {snapshot.Sessions.Count} session{(snapshot.Sessions.Count == 1 ? "" : "s")}",
                    Summary = $"Sessions that were open on {when}. Pick which to reopen.",
                    Sessions = snapshot.Sessions,
                    IsHistory = true,
                };
                choices.Add(entry);
            }
        }

        return choices;
    }

    private void CmbSnapshot_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (cmbSnapshot.SelectedItem is not SnapshotChoice choice)
        {
            return;
        }

        _rows.Clear();
        foreach (var s in choice.Sessions.OrderByDescending(s => s.LastSeenUtc))
        {
            // A session from a past snapshot that is running right now is
            // marked and starts unticked, so "Select all + Reopen" on an old
            // snapshot doesn't resume a session already open in another window.
            var alreadyOpen = _openNowIds.Contains(s.SessionId);
            var defaultSelected = !(choice.IsHistory && alreadyOpen);
            var row = new Row(s, alreadyOpen, defaultSelected);
            _rows.Add(row);
        }
        txtSummary.Text = choice.Summary;
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
        foreach (var r in _rows)
        {
            r.Selected = true;
        }
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
        {
            r.Selected = false;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private sealed class SnapshotChoice
    {
        public required string Label { get; init; }
        public required string Summary { get; init; }
        public required IReadOnlyList<OpenSessionRef> Sessions { get; init; }
        public required bool IsHistory { get; init; }
        public override string ToString() => Label;
    }

    private sealed class Row : INotifyPropertyChanged
    {
        public Row(OpenSessionRef s, bool alreadyOpen, bool defaultSelected)
        {
            Source = s;
            AlreadyOpen = alreadyOpen;
            Selected = defaultSelected;
        }

        public OpenSessionRef Source { get; }
        public bool AlreadyOpen { get; }
        public string OpenNowSuffix => AlreadyOpen ? "  ·  open now" : "";
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
            if (d < TimeSpan.FromMinutes(1))
            {
                return "just now";
            }
            if (d < TimeSpan.FromHours(1))
            {
                return $"{(int)d.TotalMinutes} min ago";
            }
            if (d < TimeSpan.FromDays(1))
            {
                return $"{(int)d.TotalHours}h ago";
            }
            return $"{(int)d.TotalDays}d ago";
        }
    }
}
