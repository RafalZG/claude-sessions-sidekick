using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Launches Claude Code inside a herdr tab (https://herdr.dev) instead of a
/// standalone terminal window. Talks to the running herdr server through its
/// CLI (<c>herdr tab create</c> / <c>herdr agent start</c> / <c>herdr pane run</c>),
/// which proxies the local socket API. Every call uses ArgumentList, so no
/// value ever passes through a shell parser.
/// </summary>
public static class HerdrService
{
    private const int TabCreateTimeoutMs = 10_000;

    // `herdr agent start` waits for the agent to become ready for input
    // (its own default is 30 s), so our process timeout must sit above it.
    private const int AgentStartTimeoutMs = 45_000;
    private const int PaneRunTimeoutMs = 10_000;
    private const int StatusTimeoutMs = 5_000;

    // How long to wait for a freshly-started herdr window to bring its server
    // up before giving up and falling back to a normal terminal.
    private const int ServerStartPollCount = 24;
    private const int ServerStartPollIntervalMs = 500;

    private const int RestoreWaitMaxMs = 20_000;
    private const int RestoreWaitIntervalMs = 1_000;
    private const long MaxSessionFileBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan RestoreMemory = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnavailableCooldown = TimeSpan.FromSeconds(60);

    private static readonly object LaunchLock = new();

    // Only touched while holding LaunchLock.
    private static HashSet<string> _restoreExpectedIds = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _restoreExpectedUntilUtc = DateTime.MinValue;
    private static DateTime _unavailableUntilUtc = DateTime.MinValue;

    public static string? FindHerdrExe()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\Herdr\bin\herdr.exe");
        return File.Exists(path) ? path : null;
    }

    public static bool IsAvailable => FindHerdrExe() != null;

    /// <summary>
    /// Creates a focused herdr tab in <paramref name="workingDir"/> and starts
    /// claude there, opening a herdr window first when none is running. Returns
    /// false when herdr is missing or a window/tab could not be created — the
    /// caller then falls back to a normal terminal launch. The slow part
    /// (waiting for claude to come up) runs on a background task after this
    /// method already returned true. Always called off the UI thread.
    /// </summary>
    public static bool TryLaunchClaude(string workingDir, string claudeArgs)
    {
        var exe = FindHerdrExe();
        if (exe == null)
        {
            AppLogger.Warn("Herdr: herdr.exe not found");
            return false;
        }

        var claudeTokens = SplitClaudeArgs(claudeArgs);

        // SplitClaudeArgs is a plain space split. That is only exact because
        // (a) this runs BEFORE ClaudeLauncherService's claude-exe-path
        // substitution, so token[0] is the bare word "claude", and (b) every
        // later token passed an allow-list that forbids spaces and quotes.
        // If a refactor breaks either guarantee, stop here — before anything
        // is created — instead of typing a mangled token into a shell pane.
        if (claudeTokens.Length == 0 || claudeTokens[0] != "claude")
        {
            AppLogger.Warn($"Herdr: unexpected command shape '{Truncate(claudeArgs)}' — aborting");
            return false;
        }

        // One launch at a time: restoring six sessions at once used to race
        // herdr's own startup and time some tab creations out.
        lock (LaunchLock)
        {
            var launched = LaunchOneAtATime(exe, workingDir, claudeArgs, claudeTokens);
            return launched;
        }
    }

    private static bool LaunchOneAtATime(string exe, string workingDir, string claudeArgs, string[] claudeTokens)
    {
        // When herdr just failed to come up, the launches queued behind it fall
        // back at once instead of each opening another window and waiting again.
        if (DateTime.UtcNow < _unavailableUntilUtc)
        {
            return false;
        }

        if (!EnsureVisibleHerdrWindow(exe, workingDir))
        {
            _unavailableUntilUtc = DateTime.UtcNow + UnavailableCooldown;
            AppLogger.Warn("Herdr: no herdr window and could not open one — falling back");
            return false;
        }

        var resumeId = FocusedSessionResolver.ExtractResumeSessionId(claudeArgs);
        if (resumeId != null && IsSessionOpenInHerdr(exe, resumeId))
        {
            AppLogger.Info($"Herdr: session {resumeId} is already open in herdr — not opening it again");
            RaiseHerdrWindow();
            return true;
        }

        var label = SanitizeLabel(Path.GetFileName(workingDir.TrimEnd('\\', '/')));
        var paneId = CreatePaneInProjectWorkspace(exe, workingDir, label);
        if (paneId == null)
        {
            return false;
        }

        Task.Run(() => StartClaudeInPane(exe, paneId, label, claudeTokens));
        RaiseHerdrWindow();
        return true;
    }

    /// <summary>
    /// Every project folder gets its own herdr workspace, named after the
    /// folder: a new tab in that workspace when it exists, otherwise a new
    /// workspace (which comes with its first tab). When the workspace list
    /// can't be read, the tab goes wherever herdr puts it, as before.
    /// Returns the pane to start claude in, or null.
    /// </summary>
    private static string? CreatePaneInProjectWorkspace(string exe, string workingDir, string label)
    {
        var listArgs = new[] { "workspace", "list" };
        var listOutput = RunCli(exe, listArgs, StatusTimeoutMs);
        string? workspaceId = null;
        var listRead = listOutput != null && TryFindWorkspaceIdByLabel(listOutput, label, out workspaceId);

        var plainTabArgs = new[] { "tab", "create", "--cwd", workingDir, "--label", label, "--focus" };
        string[] createArgs;
        if (workspaceId != null)
        {
            createArgs = new[] { "tab", "create", "--workspace", workspaceId, "--cwd", workingDir, "--label", label, "--focus" };
        }
        else if (listRead)
        {
            createArgs = new[] { "workspace", "create", "--cwd", workingDir, "--label", label, "--focus" };
        }
        else
        {
            createArgs = plainTabArgs;
        }

        var output = RunCli(exe, createArgs, TabCreateTimeoutMs);
        if (output == null && workspaceId != null)
        {
            // The workspace may have been closed a moment ago — a tab anywhere beats no tab.
            workspaceId = null;
            createArgs = plainTabArgs;
            output = RunCli(exe, createArgs, TabCreateTimeoutMs);
        }
        if (output == null)
        {
            return null;
        }

        var paneId = TryParsePaneId(output);
        if (paneId == null)
        {
            AppLogger.Warn($"Herdr: '{createArgs[0]} create' returned no pane id: {Truncate(output)}");
            return null;
        }

        // Switch the view to that workspace, in case focusing the tab alone doesn't.
        if (workspaceId != null)
        {
            var focusArgs = new[] { "workspace", "focus", workspaceId };
            RunCliRaw(exe, focusArgs, StatusTimeoutMs);
        }

        // --label on "workspace create" names the workspace only; its first
        // tab comes out as "1" (verified on herdr 0.9.5), so name it too.
        if (createArgs[0] == "workspace")
        {
            var tabId = TryParseTabId(output);
            if (tabId != null)
            {
                var renameArgs = new[] { "tab", "rename", tabId, label };
                RunCli(exe, renameArgs, StatusTimeoutMs);
            }
            else
            {
                AppLogger.Warn($"Herdr: workspace create returned no tab id: {Truncate(output)}");
            }
        }

        AppLogger.Info($"Herdr: {createArgs[0]} created, pane={paneId}, label={label}");
        return paneId;
    }

    /// <summary>
    /// Makes sure a herdr window exists before a tab is created, so the new
    /// session is actually shown. The herdr server can keep running with no
    /// window attached (the window was closed, or it was started headless), and
    /// a tab created then would be invisible. A herdr window means a running
    /// herdr client process — that works whether it lives in its own console or
    /// inside Windows Terminal. When there is none, opens one (which starts the
    /// server if it was down) and waits for the server to answer.
    /// </summary>
    private static bool EnsureVisibleHerdrWindow(string exe, string workingDir)
    {
        var hasWindow = FindHerdrClientPids().Count > 0;
        var serverRunning = IsServerRunning(exe);
        if (hasWindow && serverRunning)
        {
            return true;
        }

        // A herdr server starting from scratch reopens the sessions saved in
        // its session file — also when the user opened the herdr window just
        // before us. Read that list before the server is up, so we can wait for
        // exactly those and never open one of them a second time.
        if (!serverRunning)
        {
            RememberSessionsHerdrWillRestore();
        }

        if (!hasWindow)
        {
            AppLogger.Info("Herdr: no herdr window — opening one");
            StartHerdrWindow(exe, workingDir);
        }

        var serverUp = false;
        for (int attempt = 0; attempt < ServerStartPollCount; attempt++)
        {
            Thread.Sleep(ServerStartPollIntervalMs);
            if (IsServerRunning(exe))
            {
                AppLogger.Info($"Herdr: server reachable after {(attempt + 1) * ServerStartPollIntervalMs} ms");
                serverUp = true;
                break;
            }
        }

        if (serverUp && _restoreExpectedIds.Count > 0 && DateTime.UtcNow < _restoreExpectedUntilUtc)
        {
            WaitForHerdrRestore(exe);
        }
        return serverUp;
    }

    private static void RememberSessionsHerdrWillRestore()
    {
        var sessionFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"herdr\session.json");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sessionFileInfo = new FileInfo(sessionFile);
            if (sessionFileInfo.Exists && sessionFileInfo.Length <= MaxSessionFileBytes)
            {
                var json = File.ReadAllText(sessionFile);
                ids = ParseSavedSessionIds(json);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Herdr: could not read herdr's saved sessions: {ex.Message}");
        }

        _restoreExpectedIds = ids;
        _restoreExpectedUntilUtc = DateTime.UtcNow + RestoreMemory;
        AppLogger.Info($"Herdr: herdr will reopen {ids.Count} saved session(s) on start");
    }

    private static bool IsServerRunning(string exe)
    {
        // RunCliRaw, not RunCli, so a server that is still starting (non-zero
        // exit) doesn't log a warning on every poll during cold start.
        var statusArgs = new[] { "status", "server" };
        var result = RunCliRaw(exe, statusArgs, StatusTimeoutMs);
        var isRunning = result != null
            && result.ExitCode == 0
            && result.Stdout.Contains("status: running", StringComparison.Ordinal);
        return isRunning;
    }

    /// <summary>
    /// Opens herdr in Windows Terminal when it is installed — pasting with
    /// Ctrl+V or the right mouse button only works there, the old console
    /// window hands those keys to herdr instead of pasting. Without Windows
    /// Terminal, falls back to herdr's own console window.
    /// </summary>
    private static void StartHerdrWindow(string exe, string workingDir)
    {
        var wtExe = FindWindowsTerminalExe();

        // Windows Terminal reads ";" in its arguments as "next command", even
        // inside quotes — so the folder goes in as WorkingDirectory with "-d .",
        // and a herdr path containing ";" skips Windows Terminal altogether.
        if (wtExe != null && !exe.Contains(';'))
        {
            var wtPsi = new ProcessStartInfo
            {
                FileName = wtExe,
                WorkingDirectory = workingDir,
                UseShellExecute = false
            };
            wtPsi.ArgumentList.Add("-w");
            wtPsi.ArgumentList.Add("herdr");
            wtPsi.ArgumentList.Add("new-tab");
            wtPsi.ArgumentList.Add("-d");
            wtPsi.ArgumentList.Add(".");
            wtPsi.ArgumentList.Add(exe);
            if (TryStart(wtPsi, "Windows Terminal"))
            {
                return;
            }
        }
        else
        {
            AppLogger.Info("Herdr: not using Windows Terminal — pasting into herdr won't work in a plain console window");
        }

        var consolePsi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDir,
            UseShellExecute = true
        };
        TryStart(consolePsi, "a console window");
    }

    private static bool TryStart(ProcessStartInfo psi, string where)
    {
        try
        {
            AppLogger.Info($"Herdr: opening herdr in {where}");
            var process = Process.Start(psi);
            process?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Herdr: could not open herdr in {where}: {ex.Message}");
            return false;
        }
    }

    private static string? FindWindowsTerminalExe()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WindowsApps\wt.exe");
        var found = File.Exists(path) ? path : null;
        return found;
    }

    /// <summary>
    /// Waits until every session herdr is about to reopen shows up in its agent
    /// list. Gives up after a while — then the remembered list still stops us
    /// from opening those sessions a second time.
    /// </summary>
    private static void WaitForHerdrRestore(string exe)
    {
        var watch = Stopwatch.StartNew();
        HashSet<string>? lastOpen = null;
        while (watch.ElapsedMilliseconds < RestoreWaitMaxMs)
        {
            var open = ListSessionIdsOpenInHerdr(exe);
            if (open != null)
            {
                lastOpen = open;
                if (_restoreExpectedIds.IsSubsetOf(open))
                {
                    break;
                }
            }
            Thread.Sleep(RestoreWaitIntervalMs);
        }

        // From here herdr's own list is the truth for every session that came
        // back — a restored session the user closes later may be reopened.
        // Only the ones still missing stay remembered for a short while.
        if (lastOpen != null)
        {
            _restoreExpectedIds.ExceptWith(lastOpen);
        }

        if (_restoreExpectedIds.Count == 0)
        {
            AppLogger.Info($"Herdr: its saved sessions are back after {watch.ElapsedMilliseconds} ms");
        }
        else
        {
            AppLogger.Warn($"Herdr: after {watch.ElapsedMilliseconds} ms herdr still hasn't reopened {string.Join(", ", _restoreExpectedIds)}");
        }
    }

    /// <summary>
    /// True when herdr shows the session now, or is still expected to reopen it
    /// from its saved state. When herdr can't be asked, false — a second copy
    /// is better than a lost session.
    /// </summary>
    private static bool IsSessionOpenInHerdr(string exe, string sessionId)
    {
        var expectedFromRestore = DateTime.UtcNow < _restoreExpectedUntilUtc
            && _restoreExpectedIds.Contains(sessionId);
        if (expectedFromRestore)
        {
            AppLogger.Warn($"Herdr: not opening {sessionId} — herdr was about to reopen it itself; reopen it again if it doesn't show up");
            return true;
        }

        var open = ListSessionIdsOpenInHerdr(exe);
        if (open == null)
        {
            Thread.Sleep(RestoreWaitIntervalMs);
            open = ListSessionIdsOpenInHerdr(exe);
        }
        if (open == null)
        {
            AppLogger.Warn($"Herdr: could not ask herdr which sessions are open — opening {sessionId} anyway");
            return false;
        }

        var isOpen = open.Contains(sessionId);
        return isOpen;
    }

    /// <summary>Null when herdr could not be asked — not the same as "none open".</summary>
    private static HashSet<string>? ListSessionIdsOpenInHerdr(string exe)
    {
        var listArgs = new[] { "agent", "list" };
        var result = RunCliRaw(exe, listArgs, StatusTimeoutMs);
        if (result == null || result.ExitCode != 0)
        {
            return null;
        }

        var ids = ParseOpenSessionIds(result.Stdout);
        return ids;
    }

    /// <summary>
    /// Claude session ids from <c>herdr agent list</c>
    /// (<c>result.agents[].agent_session.value</c>).
    /// </summary>
    internal static HashSet<string> ParseOpenSessionIds(string json)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetResultObject(doc.RootElement, out var result)
                || !result.TryGetProperty("agents", out var agents)
                || agents.ValueKind != JsonValueKind.Array)
            {
                return ids;
            }

            foreach (var agent in agents.EnumerateArray())
            {
                AddSessionIdOf(agent, ids);
            }
        }
        catch (JsonException)
        {
            // Unreadable output = nothing known to be open.
        }
        return ids;
    }

    /// <summary>
    /// Claude session ids herdr saved in its session file — every pane that
    /// carries an <c>agent_session</c>, wherever it sits in the file. Searched
    /// for rather than read along a fixed path, because the file is herdr's
    /// own and its layout may change between versions.
    /// </summary>
    internal static HashSet<string> ParseSavedSessionIds(string json)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            CollectSessionIds(doc.RootElement, ids);
        }
        catch (JsonException)
        {
            // Unreadable file = herdr will reopen nothing we know of.
        }
        return ids;
    }

    private static void CollectSessionIds(JsonElement element, HashSet<string> ids)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            AddSessionIdOf(element, ids);
            foreach (var property in element.EnumerateObject())
            {
                CollectSessionIds(property.Value, ids);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectSessionIds(item, ids);
            }
        }
    }

    private static void AddSessionIdOf(JsonElement holder, HashSet<string> ids)
    {
        if (holder.ValueKind != JsonValueKind.Object
            || !holder.TryGetProperty("agent_session", out var session)
            || session.ValueKind != JsonValueKind.Object
            || !session.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        // Stored in the dashed form, the only one a --resume argument uses.
        if (Guid.TryParse(value.GetString(), out var guid))
        {
            var normalized = guid.ToString("D");
            ids.Add(normalized);
        }
    }

    /// <summary>
    /// Herdr processes that are windows the user looks at, as opposed to the
    /// background server and our own short CLI calls.
    /// </summary>
    private static List<int> FindHerdrClientPids()
    {
        var clientPids = new List<int>();
        foreach (var process in Process.GetProcessesByName("herdr"))
        {
            var pid = process.Id;
            process.Dispose();
            // An unreadable command line (e.g. herdr running as administrator)
            // counts as a window, rather than opening yet another one.
            var commandLine = ClaudeProcessService.GetCommandLine(pid);
            if (commandLine == null || IsHerdrClientCommandLine(commandLine))
            {
                clientPids.Add(pid);
            }
        }
        return clientPids;
    }

    // First words that make herdr run a command and exit (or be the server)
    // rather than show a window. Anything else counts as a window, so an
    // unknown new flag errs towards "a window is there" — never towards
    // opening yet another one.
    private static readonly HashSet<string> NonWindowFirstWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "server", "status", "update", "completion", "channel", "machine", "api", "config",
        "workspace", "worktree", "tab", "notification", "agent", "pane", "integration",
        "--machine", "--help", "-h", "--version", "-V", "--skill"
    };

    internal static bool IsHerdrClientCommandLine(string commandLine)
    {
        var trimmed = commandLine.Trim();
        var exeEnd = FindExeEnd(trimmed);
        string rest;
        if (exeEnd >= 0)
        {
            rest = trimmed[exeEnd..].TrimStart('"');
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            rest = space < 0 ? "" : trimmed[(space + 1)..];
        }

        var args = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0)
        {
            return true;
        }

        if (args[0].Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            var isAttach = args.Length > 1 && args[1].Equals("attach", StringComparison.OrdinalIgnoreCase);
            return isAttach;
        }

        var isClient = !NonWindowFirstWords.Contains(args[0]);
        return isClient;
    }

    /// <summary>
    /// Position right after the first ".exe" that really ends the program path —
    /// followed by a quote, whitespace or the end — so a folder such as
    /// "joe.exeter" isn't mistaken for it. -1 when there is none.
    /// </summary>
    private static int FindExeEnd(string commandLine)
    {
        var from = 0;
        while (true)
        {
            var index = commandLine.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var end = index + 4;
            if (end == commandLine.Length || commandLine[end] == '"' || char.IsWhiteSpace(commandLine[end]))
            {
                return end;
            }
            from = end;
        }
    }

    /// <summary>
    /// Best-effort: brings the herdr window to the foreground so a tab created
    /// in an already-running, backgrounded herdr is visible. Windows'
    /// foreground rules can also turn SetForegroundWindow into a taskbar flash
    /// rather than a real raise when Sidekick doesn't hold the foreground — that
    /// is expected, not a failure.
    /// </summary>
    private static void RaiseHerdrWindow()
    {
        try
        {
            var hWnd = FindHerdrWindow();
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SW_RESTORE);
            }
            SetForegroundWindow(hWnd);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Herdr: raising the herdr window failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The window showing a herdr client, or <see cref="IntPtr.Zero"/>. A client
    /// in its own console owns its window directly. A client inside Windows
    /// Terminal doesn't — the window belongs to the WindowsTerminal process,
    /// which usually holds ALL of the user's terminal windows. There only the
    /// window whose title herdr set ("COMPUTER: tab") counts; better to raise
    /// nothing than some unrelated terminal.
    /// </summary>
    private static IntPtr FindHerdrWindow()
    {
        var clientPids = FindHerdrClientPids();
        if (clientPids.Count == 0)
        {
            return IntPtr.Zero;
        }

        var herdrPids = new HashSet<uint>();
        var terminalPids = new HashSet<uint>();
        foreach (var pid in clientPids)
        {
            herdrPids.Add((uint)pid);
            var terminalPid = FocusedSessionService.FindAncestorPid(pid, "WindowsTerminal.exe");
            if (terminalPid != null)
            {
                terminalPids.Add((uint)terminalPid.Value);
            }
        }

        var titlePrefixes = new[] { Environment.MachineName + ":", System.Net.Dns.GetHostName() + ":" };
        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (herdrPids.Contains(windowPid))
            {
                found = hWnd;
                return false;
            }
            if (!terminalPids.Contains(windowPid))
            {
                return true;
            }

            var title = GetWindowTitle(hWnd);
            if (titlePrefixes.Any(p => title.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        var title = buffer.ToString();
        return title;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static void StartClaudeInPane(string exe, string paneId, string label, string[] claudeTokens)
    {
        // Preferred path: register claude as a herdr agent so the pane gets
        // status dots and notifications. Args after "--" go to claude itself.
        var agentName = BuildAgentName(label);
        var startArgs = new List<string> { "agent", "start", agentName, "--kind", "claude", "--pane", paneId, "--" };
        startArgs.AddRange(claudeTokens.Skip(1));
        var arrStartArgs = startArgs.ToArray();
        var started = RunCli(exe, arrStartArgs, AgentStartTimeoutMs);
        if (started == null)
        {
            // A just-created pane can report "not an available shell" until
            // its PowerShell prompt appears — give it a moment and try again.
            Thread.Sleep(1500);
            started = RunCli(exe, arrStartArgs, AgentStartTimeoutMs);
        }
        if (started != null)
        {
            AppLogger.Info($"Herdr: agent '{agentName}' started in pane {paneId}");
            return;
        }

        // Fallback: type the plain command into the pane's shell. Herdr's own
        // agent auto-detection usually still picks claude up from there.
        // When `agent start` reported failure but claude actually came up
        // (CLI timeout after a successful start), the pane already hosts an
        // agent — typing the fallback command would land inside claude's
        // input box. Verified against herdr 0.9.5: `agent get` exits 0 with
        // agent JSON when a pane hosts one, and prints agent_not_found
        // otherwise. Fail closed: only an explicit agent_not_found proves the
        // pane is free — a timeout or CLI error skips the fallback instead.
        var getArgs = new[] { "agent", "get", paneId };
        var occupantResult = RunCliRaw(exe, getArgs, PaneRunTimeoutMs);
        var paneIsFree = occupantResult != null
            && occupantResult.ExitCode != 0
            && (occupantResult.Stdout.Contains("agent_not_found", StringComparison.Ordinal)
                || occupantResult.Stderr.Contains("agent_not_found", StringComparison.Ordinal));
        if (!paneIsFree)
        {
            AppLogger.Info($"Herdr: pane {paneId} not confirmed free — skipping pane run fallback");
            return;
        }

        AppLogger.Warn($"Herdr: agent start failed in pane {paneId}, falling back to pane run");
        // No "--" separator here: verified against herdr 0.9.5 that everything
        // after the pane id — option-shaped tokens like --resume/-c included —
        // is typed into the pane verbatim, while a "--" would land in the
        // shell literally and break the command.
        var runArgs = new List<string> { "pane", "run", paneId };
        runArgs.AddRange(claudeTokens);
        var arrRunArgs = runArgs.ToArray();
        var ran = RunCli(exe, arrRunArgs, PaneRunTimeoutMs);
        if (ran == null)
        {
            AppLogger.Warn($"Herdr: pane run also failed in pane {paneId} — pane left at shell prompt");
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private static string? RunCli(string exe, string[] args, int timeoutMs)
    {
        var result = RunCliRaw(exe, args, timeoutMs);
        if (result == null)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            AppLogger.Warn($"Herdr: '{args[0]} {args[1]}' exited {result.ExitCode}: {Truncate(result.Stderr)}");
            return null;
        }

        return result.Stdout;
    }

    private static CliResult? RunCliRaw(string exe, string[] args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var process = Process.Start(psi);
            if (process == null)
            {
                AppLogger.Warn("Herdr: Process.Start returned null");
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already exited between the wait and the kill.
                }
                AppLogger.Warn($"Herdr: '{args[0]} {args[1]}' timed out after {timeoutMs} ms");
                return null;
            }

            // WaitForExit(timeout) does not wait for the redirected pipes to
            // drain, and a lingering child that inherited the handles can hold
            // them open forever — bound the drain too.
            var drainTasks = new Task[] { stdout, stderr };
            if (!Task.WaitAll(drainTasks, 2_000))
            {
                AppLogger.Warn($"Herdr: '{args[0]} {args[1]}' exited but its output pipes stayed open");
                return null;
            }

            var cliResult = new CliResult(process.ExitCode, stdout.Result, stderr.Result);
            return cliResult;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Herdr: CLI call failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pulls <c>result.root_pane.pane_id</c> out of the JSON that
    /// <c>herdr tab create</c> and <c>herdr workspace create</c> print. Null when the shape doesn't match or
    /// the id contains characters outside herdr's <c>w1:p2</c>-style format.
    /// </summary>
    internal static string? TryParsePaneId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetResultObject(doc.RootElement, out var result)
                || !result.TryGetProperty("root_pane", out var rootPane)
                || rootPane.ValueKind != JsonValueKind.Object
                || !rootPane.TryGetProperty("pane_id", out var paneIdElement))
            {
                return null;
            }

            var paneId = paneIdElement.ValueKind == JsonValueKind.String ? paneIdElement.GetString() : null;
            var safePaneId = IsSafeHerdrId(paneId) ? paneId : null;
            return safePaneId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Looks up the workspace named <paramref name="label"/> in
    /// <c>herdr workspace list</c> output. Returns false when the output can't
    /// be understood — then nobody can tell whether the workspace exists, so the
    /// caller must not create another one. True with a null id means it was
    /// read and there is no such workspace.
    /// </summary>
    internal static bool TryFindWorkspaceIdByLabel(string json, string label, out string? workspaceId)
    {
        workspaceId = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetResultObject(doc.RootElement, out var result)
                || !result.TryGetProperty("workspaces", out var workspaces)
                || workspaces.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var workspace in workspaces.EnumerateArray())
            {
                if (workspace.ValueKind != JsonValueKind.Object
                    || !workspace.TryGetProperty("label", out var labelElement)
                    || labelElement.ValueKind != JsonValueKind.String
                    || !workspace.TryGetProperty("workspace_id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                if (string.Equals(labelElement.GetString(), label, StringComparison.OrdinalIgnoreCase))
                {
                    var foundId = idElement.GetString();
                    if (!IsSafeHerdrId(foundId))
                    {
                        return false;
                    }
                    workspaceId = foundId;
                    return true;
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary><c>result.root_pane.tab_id</c> from <c>workspace create</c> / <c>tab create</c> output, or null.</summary>
    internal static string? TryParseTabId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetResultObject(doc.RootElement, out var result)
                || !result.TryGetProperty("root_pane", out var rootPane)
                || rootPane.ValueKind != JsonValueKind.Object
                || !rootPane.TryGetProperty("tab_id", out var tabIdElement)
                || tabIdElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var tabId = tabIdElement.GetString();
            var safeTabId = IsSafeHerdrId(tabId) ? tabId : null;
            return safeTabId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // TryGetProperty throws on anything that isn't a JSON object, so check first.
    private static bool TryGetResultObject(JsonElement root, out JsonElement result)
    {
        result = default;
        var found = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("result", out result)
            && result.ValueKind == JsonValueKind.Object;
        return found;
    }

    /// <summary>
    /// Herdr ids look like <c>w1</c> or <c>w1:p2</c>. The first character must
    /// be a letter or digit so an id can never look like a CLI flag
    /// ("--version") when passed back to herdr.
    /// </summary>
    private static bool IsSafeHerdrId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !char.IsAsciiLetterOrDigit(id[0]))
        {
            return false;
        }

        foreach (var c in id)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c == ':' || c == '-' || c == '_';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Tab label shown in herdr's UI, derived from the project folder name.
    /// Letters/digits (any alphabet) plus a few safe punctuation marks; the
    /// value never reaches a shell (ArgumentList), this just keeps the UI tidy.
    /// </summary>
    internal static string SanitizeLabel(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return "claude";
        }

        var kept = rawName.Trim()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')')
            .ToArray();
        var joined = new string(kept);
        // Starts with a letter or digit, so a folder named "--help" can't turn
        // the label into something herdr reads as an option.
        var fromFirstLetter = joined
            .SkipWhile(c => !char.IsLetterOrDigit(c))
            .ToArray();
        var startsClean = new string(fromFirstLetter);
        var label = startsClean.TrimEnd();
        if (label.Length == 0)
        {
            return "claude";
        }
        if (label.Length > 48)
        {
            label = label[..48].TrimEnd();
        }
        return label;
    }

    /// <summary>
    /// Herdr agent names must match <c>[a-z][a-z0-9_-]{0,31}</c> and be unique
    /// among live agents. Built from the tab label plus a random suffix so two
    /// sessions in the same project never collide.
    /// </summary>
    internal static string BuildAgentName(string label)
    {
        var kept = label.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .SkipWhile(c => !char.IsAsciiLetterLower(c))
            .ToArray();
        var joined = new string(kept);
        var baseName = joined.Trim('-');
        if (baseName.Length == 0)
        {
            baseName = "claude";
        }
        if (baseName.Length > 26)
        {
            baseName = baseName[..26].Trim('-');
        }

        var suffix = Random.Shared.Next(0x10000).ToString("x4");
        var agentName = $"{baseName}-{suffix}";
        return agentName;
    }

    /// <summary>
    /// Splits the launcher's command string ("claude --resume &lt;guid&gt; --model opus")
    /// into tokens. Every component is already validated upstream (GUID check,
    /// model/effort allow-lists), so a plain space split is exact.
    /// </summary>
    internal static string[] SplitClaudeArgs(string claudeArgs)
    {
        var tokens = claudeArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens;
    }

    /// <summary>
    /// Caps length and swaps control characters for spaces, so a line coming
    /// from herdr's stdout/stderr can never pose as extra log lines.
    /// </summary>
    private static string Truncate(string text)
    {
        var sanitized = text
            .Select(c => c < ' ' || c == '\x7F' ? ' ' : c)
            .ToArray();
        var joined = new string(sanitized);
        var trimmed = joined.Trim();
        var capped = trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed;
        return capped;
    }
}
