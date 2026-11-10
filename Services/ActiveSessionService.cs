using System.IO;
using System.Management;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Name and PID of a live claude process found resuming a session, so the
/// duplicate-session dialog can show the user exactly what it matched.
/// </summary>
public sealed record ActiveResumeProcess(string ProcessName, uint ProcessId);

public static class ActiveSessionService
{
    // Shells and terminal hosts that Sidekick itself (or the user) launches
    // around claude. They keep "--resume <uuid>" in their own command line
    // forever — cmd /k and powershell -NoExit stay open after claude exits —
    // so matching them reported "Session already open" for sessions whose
    // claude ended hours ago. Only a non-wrapper match (node/claude/bun)
    // means a claude process is really attached to the session.
    private static readonly string[] WrapperProcessNames =
    {
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe",
        "bash.exe",
        "sh.exe",
        "wt.exe",
        "windowsterminal.exe",
        "openconsole.exe",
        "conhost.exe",
        "mintty.exe",
    };

    internal static bool IsWrapperProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }
        var name = Path.GetFileName(processName.Trim());
        foreach (var wrapper in WrapperProcessNames)
        {
            if (string.Equals(name, wrapper, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Best-effort check for a currently-running <c>claude --resume {sessionId}</c>
    /// process. WMI is used because Windows has no other userspace mechanism for
    /// reading a process's command line (Process.StartInfo is empty for processes
    /// you didn't start). Returns null on any error so the caller defaults to
    /// allowing the launch — a false negative (missing a duplicate) is a worse
    /// failure mode than a false positive (blocking a legitimate resume), and
    /// the consequence (file conflict) is non-fatal, so we err toward letting
    /// the user through and trust them to know.
    /// </summary>
    public static ActiveResumeProcess? FindResumeProcess(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        try
        {
            // WHERE narrows the result set so we don't iterate every process on
            // the box just to look at <100 with "--resume" in their command line.
            // The .Contains() below confirms the exact UUID — session IDs are
            // version-4 GUIDs (122 bits of entropy) so substring collisions are
            // effectively impossible.
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId, CommandLine FROM Win32_Process WHERE CommandLine LIKE '%--resume%'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                var cmd = obj["CommandLine"] as string;
                if (string.IsNullOrEmpty(cmd)
                    || !cmd.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = obj["Name"] as string;
                if (IsWrapperProcess(name))
                {
                    continue;
                }

                var pid = obj["ProcessId"] is uint u ? u : 0u;
                var displayName = string.IsNullOrWhiteSpace(name) ? "unknown process" : name;
                return new ActiveResumeProcess(displayName, pid);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"FindResumeProcess: WMI query failed: {ex.Message}");
        }

        return null;
    }
}
