using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Detects how many Claude Code CLI processes are currently running.
/// Used to filter "active sessions" so closed Claude windows stop being counted
/// just because their JSONL was written to recently.
///
/// Claude Code CLI runs as `node.exe` with a command line containing
/// `@anthropic-ai\claude-code\cli.js`. We enumerate node.exe processes and
/// query each one's command line via the NT process information API.
/// </summary>
public static class ClaudeProcessService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly object _lock = new();
    private static int _cachedCount = -1;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// Returns the number of currently-running Claude Code CLI processes,
    /// or -1 if the scan failed and the caller should fall back to other heuristics.
    /// Result is cached for a short interval to avoid hammering NtQueryInformationProcess.
    /// </summary>
    public static int GetRunningClaudeCodeCount()
    {
        lock (_lock)
        {
            if (_cachedCount >= 0 && DateTime.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cachedCount;
            }

            try
            {
                int count = 0;

                // CLI: node.exe running @anthropic-ai/claude-code/cli.js
                foreach (var p in Process.GetProcessesByName("node"))
                {
                    try
                    {
                        var cmdLine = GetCommandLine(p.Id);
                        if (cmdLine != null &&
                            cmdLine.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            cmdLine.IndexOf("cli.js", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            count++;
                        }
                    }
                    catch
                    {
                        // Ignore individual process query failures (e.g. permission denied)
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }

                // Desktop App: Claude.exe (Electron-based, runs on Win 11 / macOS)
                foreach (var p in Process.GetProcessesByName("Claude"))
                {
                    count++;
                    p.Dispose();
                }

                _cachedCount = count;
                _cachedAt = DateTime.UtcNow;
                return count;
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"ClaudeProcessService scan failed: {ex.Message}");
                _cachedCount = -1;
                _cachedAt = DateTime.UtcNow;
                return -1;
            }
        }
    }

    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cachedCount = -1;
            _cachedAt = DateTime.MinValue;
        }
    }

    // ---- P/Invoke: read another process's command line via NtQueryInformationProcess ----
    // ProcessCommandLineInformation (class 60) is supported on Windows 8.1+ and returns
    // a UNICODE_STRING with the full command line. No need for WMI / System.Management.

    private const int ProcessCommandLineInformation = 60;
    private const int STATUS_SUCCESS = 0;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        IntPtr ProcessInformation,
        uint ProcessInformationLength,
        out uint ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static string? GetCommandLine(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // Probe for required buffer size
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out uint needed);
            if (needed == 0)
            {
                return null;
            }

            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buf, needed, out _);
                if (status != STATUS_SUCCESS)
                {
                    return null;
                }

                var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                if (us.Buffer == IntPtr.Zero || us.Length == 0)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(us.Buffer, us.Length / sizeof(char));
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
