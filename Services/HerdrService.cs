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

        if (!EnsureVisibleHerdrWindow(exe, workingDir))
        {
            AppLogger.Warn("Herdr: no herdr window and could not open one — falling back");
            return false;
        }

        var label = SanitizeLabel(Path.GetFileName(workingDir));
        var createArgs = new[] { "tab", "create", "--cwd", workingDir, "--label", label, "--focus" };
        var output = RunCli(exe, createArgs, TabCreateTimeoutMs);
        if (output == null)
        {
            return false;
        }

        var paneId = TryParsePaneId(output);
        if (paneId == null)
        {
            AppLogger.Warn($"Herdr: tab create returned no pane id: {Truncate(output)}");
            return false;
        }

        AppLogger.Info($"Herdr: tab created, pane={paneId}, label={label}");
        Task.Run(() => StartClaudeInPane(exe, paneId, label, claudeTokens));
        RaiseHerdrWindow();
        return true;
    }

    /// <summary>
    /// Makes sure a visible herdr window exists before a tab is created, so the
    /// new session is actually shown. The herdr server can keep running with no
    /// window attached (the window was closed, or it was started headless), and
    /// a tab created then would be invisible. When no herdr-owned window is
    /// found, opens one — which starts the server if it was down and otherwise
    /// just attaches — and waits for the server to answer. Returns false if the
    /// server never comes up.
    /// </summary>
    private static bool EnsureVisibleHerdrWindow(string exe, string workingDir)
    {
        if (FindHerdrWindow() != IntPtr.Zero && IsServerRunning(exe))
        {
            return true;
        }

        AppLogger.Info("Herdr: no visible herdr window — opening one");
        StartHerdrWindow(exe, workingDir);

        for (int attempt = 0; attempt < ServerStartPollCount; attempt++)
        {
            Thread.Sleep(ServerStartPollIntervalMs);
            if (IsServerRunning(exe))
            {
                AppLogger.Info($"Herdr: server reachable after {(attempt + 1) * ServerStartPollIntervalMs} ms");
                return true;
            }
        }

        return false;
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
    /// Opens herdr in its own console window. Deliberately not via Windows
    /// Terminal: a wt-hosted herdr renders inside the WindowsTerminal process,
    /// so its window can't be attributed back to a herdr process, which would
    /// defeat <see cref="FindHerdrWindow"/> and spawn a fresh window on every
    /// launch. A herdr console window is owned by the herdr process itself, so
    /// it can be found and reused. A new top-level window appears and takes
    /// focus, so the session the caller is about to create shows straight away.
    /// </summary>
    private static void StartHerdrWindow(string exe, string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };
            var process = Process.Start(psi);
            process?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Herdr: could not open a herdr window: {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort: brings a herdr-owned window to the foreground so a tab
    /// created in an already-running, backgrounded herdr is visible. When herdr
    /// renders inside a host terminal that owns the window (no window handle on
    /// any herdr process), this safely finds nothing and does nothing. Windows'
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
    /// First visible top-level window owned by any herdr process, or
    /// <see cref="IntPtr.Zero"/> when none — the server process has no window,
    /// so this skips it and finds the window of a console-hosted herdr client.
    /// Any herdr client window is fine to raise: they all attach to the same
    /// session, so "first in z-order" needs no main-window preference.
    /// </summary>
    private static IntPtr FindHerdrWindow()
    {
        var herdrPids = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("herdr"))
        {
            herdrPids.Add((uint)process.Id);
            process.Dispose();
        }
        if (herdrPids.Count == 0)
        {
            return IntPtr.Zero;
        }

        var found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (!herdrPids.Contains(windowPid))
            {
                return true;
            }
            found = hWnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

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
    /// <c>herdr tab create</c> prints. Null when the shape doesn't match or
    /// the id contains characters outside herdr's <c>w1:p2</c>-style format.
    /// </summary>
    internal static string? TryParsePaneId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("root_pane", out var rootPane)
                || !rootPane.TryGetProperty("pane_id", out var paneIdElement))
            {
                return null;
            }

            var paneId = paneIdElement.GetString();
            if (string.IsNullOrEmpty(paneId) || paneId.Length > 64)
            {
                return null;
            }

            // First char must be alphanumeric so the id can never look like a
            // CLI flag ("--version") when passed back to herdr.
            if (!char.IsAsciiLetterOrDigit(paneId[0]))
            {
                return null;
            }

            foreach (var c in paneId)
            {
                bool ok = (c >= 'a' && c <= 'z')
                       || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9')
                       ||  c == ':' || c == '-' || c == '_';
                if (!ok)
                {
                    return null;
                }
            }

            return paneId;
        }
        catch (JsonException)
        {
            return null;
        }
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
        var label = joined.Trim();
        if (label.Length == 0)
        {
            return "claude";
        }
        if (label.Length > 48)
        {
            label = label[..48];
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
