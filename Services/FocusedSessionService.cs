using System.Runtime.InteropServices;
using System.Text;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Finds the Claude session shown in the window the user currently has in
/// focus, so "Copy Last Reply" can copy from THAT session instead of the
/// newest one across all windows (GitHub issues #2/#3).
///
/// How: the focused window belongs to a terminal (Windows Terminal, VS Code,
/// conhost…). Claude Code runs inside it as a child process (claude.exe, or
/// node.exe for npm installs). So we
/// take the foreground window's process, walk every running Claude CLI
/// process's parent chain, and keep the ones that live under that window.
/// Each match is then mapped to a tracked session by its --resume session ID
/// or by its working directory. All of it is best-effort: any failure returns
/// null and the caller falls back to the old "newest overall" behavior.
/// </summary>
public static class FocusedSessionService
{
    public static SessionTokenData? ResolveForegroundSession(IReadOnlyList<SessionTokenData> sessions)
    {
        try
        {
            // Called only on the copy hotkey, so the diagnostic trace below is
            // cheap. It exists to debug "copied from the wrong session" reports
            // from the log alone — it records every decision the resolver makes.
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                AppLogger.Info("FocusResolve: no foreground window -> fallback");
                return null;
            }

            GetWindowThreadProcessId(hwnd, out var foregroundPid);
            if (foregroundPid == 0)
            {
                AppLogger.Info("FocusResolve: foreground window has no pid -> fallback");
                return null;
            }

            var processes = SnapshotProcesses();
            if (processes.Count == 0)
            {
                AppLogger.Info("FocusResolve: process snapshot empty -> fallback");
                return null;
            }

            var fgName = processes.TryGetValue((int)foregroundPid, out var fg) ? fg.Name : "?";
            AppLogger.Info($"FocusResolve: foreground pid={foregroundPid} exe={fgName}");

            // Classic console windows are owned by conhost.exe, which is a
            // CHILD of the shell — the claude process is not its descendant.
            // Step up to conhost's parent (the shell) before walking chains.
            if (processes.TryGetValue((int)foregroundPid, out var fgEntry)
                && fgEntry.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
            {
                foregroundPid = (uint)fgEntry.ParentPid;
                AppLogger.Info($"FocusResolve: conhost step-up -> pid={foregroundPid}");
            }

            var parentOf = new Dictionary<int, int>(processes.Count);
            foreach (var kv in processes)
            {
                parentOf[kv.Key] = kv.Value.ParentPid;
            }

            // A claude launched with its own console window (Explorer,
            // `start claude`) IS the foreground process after the conhost
            // step-up — accept it as well as descendants. Collect ALL claude
            // processes first so the trace also shows the ones rejected by
            // the ancestry test — the most likely silent failure.
            var fgPid = (int)foregroundPid;
            var all = CollectClaudeProcesses(processes, pidFilter: null);
            var matched = new List<FocusedClaudeProcess>();
            foreach (var proc in all)
            {
                var isUnderWindow = proc.Pid == fgPid
                    || FocusedSessionResolver.IsAncestor(fgPid, proc.Pid, parentOf);
                var resumeId = FocusedSessionResolver.ExtractResumeSessionId(proc.CommandLine) ?? "none";
                AppLogger.Info($"FocusResolve: claude pid={proc.Pid} underWindow={isUnderWindow} resume={resumeId} cwd={proc.WorkingDirectory ?? "?"}");
                if (isUnderWindow)
                {
                    matched.Add(proc);
                }
            }

            if (matched.Count == 0)
            {
                AppLogger.Info($"FocusResolve: none of {all.Count} claude process(es) under foreground window -> fallback");
                return null;
            }

            var title = GetWindowTitle(hwnd);
            AppLogger.Info($"FocusResolve: window title=\"{title ?? "?"}\"");
            var resolved = FocusedSessionResolver.Resolve(sessions, matched, title, AppLogger.Info);
            AppLogger.Info(resolved == null
                ? "FocusResolve: no session matched -> fallback"
                : $"FocusResolve: RESULT session={resolved.SessionId} ({resolved.CustomName ?? resolved.Slug ?? resolved.ProjectName})");
            return resolved;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"FocusedSessionService: resolve failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// All Claude Code CLI processes currently running, regardless of which
    /// window they live under. Used by session-restore snapshots to know
    /// exactly which sessions are open. Best-effort: failure = empty list.
    /// </summary>
    public static List<FocusedClaudeProcess> EnumerateRunningClaudeProcesses()
    {
        try
        {
            var processes = SnapshotProcesses();
            var found = CollectClaudeProcesses(processes, pidFilter: null);
            return found;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"FocusedSessionService: enumerate failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Walks the process snapshot and keeps the Claude CLI processes
    /// (optionally only those accepted by <paramref name="pidFilter"/>),
    /// each with its command line and working directory.
    /// </summary>
    private static List<FocusedClaudeProcess> CollectClaudeProcesses(
        Dictionary<int, ProcessEntry> processes
        , Func<int, bool>? pidFilter)
    {
        var matched = new List<FocusedClaudeProcess>();
        foreach (var kv in processes)
        {
            var name = kv.Value.Name;
            var mayBeClaude = name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("claude.exe", StringComparison.OrdinalIgnoreCase);
            if (!mayBeClaude)
            {
                continue;
            }

            if (pidFilter != null && !pidFilter(kv.Key))
            {
                continue;
            }

            var cmdLine = ClaudeProcessService.GetCommandLine(kv.Key);
            if (!FocusedSessionResolver.IsClaudeCliProcess(name, cmdLine))
            {
                continue;
            }

            var cwd = ReadProcessCurrentDirectory(kv.Key);
            var proc = new FocusedClaudeProcess(kv.Key, cmdLine ?? "", cwd);
            matched.Add(proc);
        }

        return matched;
    }

    // ---- process snapshot (pid -> parent pid + exe name) ----

    private readonly record struct ProcessEntry(int ParentPid, string Name);

    private static Dictionary<int, ProcessEntry> SnapshotProcesses()
    {
        var map = new Dictionary<int, ProcessEntry>(512);
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == INVALID_HANDLE_VALUE)
        {
            return map;
        }

        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return map;
            }

            do
            {
                map[(int)entry.th32ProcessID] = new ProcessEntry((int)entry.th32ParentProcessID, entry.szExeFile);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        var copied = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        if (copied <= 0)
        {
            return null;
        }

        var title = buffer.ToString();
        return title;
    }

    // ---- reading another process's working directory ----
    // The command line tells us WHICH claude ran, but only the working
    // directory tells us which PROJECT it runs in. Windows keeps it in the
    // process's PEB, readable with ReadProcessMemory. 64-bit offsets only —
    // this app and Claude Code's node.exe are both x64.

    private const long PebProcessParametersOffset = 0x20;
    private const long ParametersCurrentDirectoryOffset = 0x38;

    private static string? ReadProcessCurrentDirectory(int pid)
    {
        if (!Environment.Is64BitProcess)
        {
            return null;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(
                handle, 0, ref pbi, (uint)Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            if (!ReadPointer(handle, pbi.PebBaseAddress + (int)PebProcessParametersOffset, out var parameters)
                || parameters == IntPtr.Zero)
            {
                return null;
            }

            var unicodeStringAddress = parameters + (int)ParametersCurrentDirectoryOffset;
            var raw = new byte[16];
            if (!ReadBytes(handle, unicodeStringAddress, raw))
            {
                return null;
            }

            var stringLength = BitConverter.ToUInt16(raw, 0); // bytes, not chars
            var stringBuffer = (IntPtr)BitConverter.ToInt64(raw, 8);
            if (stringLength == 0 || stringLength > 4096 || stringBuffer == IntPtr.Zero)
            {
                return null;
            }

            var chars = new byte[stringLength];
            if (!ReadBytes(handle, stringBuffer, chars))
            {
                return null;
            }

            var cwd = Encoding.Unicode.GetString(chars);
            return cwd;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool ReadPointer(IntPtr process, IntPtr address, out IntPtr value)
    {
        var buffer = new byte[8];
        if (!ReadBytes(process, address, buffer))
        {
            value = IntPtr.Zero;
            return false;
        }

        value = (IntPtr)BitConverter.ToInt64(buffer, 0);
        return true;
    }

    private static bool ReadBytes(IntPtr process, IntPtr address, byte[] buffer)
    {
        var ok = ReadProcessMemory(process, address, buffer, buffer.Length, out var read);
        return ok && read == buffer.Length;
    }

    // ---- P/Invoke ----

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess
        , IntPtr lpBaseAddress
        , byte[] lpBuffer
        , int nSize
        , out int lpNumberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle
        , int processInformationClass
        , ref PROCESS_BASIC_INFORMATION processInformation
        , uint processInformationLength
        , out uint returnLength);
}
