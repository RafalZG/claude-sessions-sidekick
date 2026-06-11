using System.Management;

namespace ClaudeSessionsSidekick.Services;

public static class ActiveSessionService
{
    /// <summary>
    /// Best-effort check for a currently-running <c>claude --resume {sessionId}</c>
    /// process. WMI is used because Windows has no other userspace mechanism for
    /// reading a process's command line (Process.StartInfo is empty for processes
    /// you didn't start). Returns false on any error so the caller defaults to
    /// allowing the launch — a false negative (missing a duplicate) is a worse
    /// failure mode than a false positive (blocking a legitimate resume), and
    /// the consequence (file conflict) is non-fatal, so we err toward letting
    /// the user through and trust them to know.
    /// </summary>
    public static bool IsResumeRunning(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        try
        {
            // WHERE narrows the result set so we don't iterate every process on
            // the box just to look at <100 with "--resume" in their command line.
            // The .Contains() below confirms the exact UUID — session IDs are
            // version-4 GUIDs (122 bits of entropy) so substring collisions are
            // effectively impossible.
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE CommandLine LIKE '%--resume%'");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                var cmd = obj["CommandLine"] as string;
                if (!string.IsNullOrEmpty(cmd)
                    && cmd.Contains(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"IsResumeRunning: WMI query failed: {ex.Message}");
        }

        return false;
    }
}
