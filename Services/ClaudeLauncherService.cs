using System.Diagnostics;
using System.IO;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

public static class ClaudeLauncherService
{
    public static ShellType PreferredShell { get; set; } = ShellType.Auto;
    public static string? ClaudeExePath { get; set; }

    public static void Launch(QuickLaunchEntry entry)
    {
        if (!Directory.Exists(entry.FolderPath))
        {
            return;
        }

        var claudeCmd = entry.ContinueLastSession ? "claude -c" : "claude";
        claudeCmd += BuildModelArg(entry.ModelOverride);
        LaunchInShell(entry.FolderPath, claudeCmd, entry.ShellOverride);
    }

    public static void LaunchResume(QuickLaunchEntry entry, string sessionId, string? effortOverride = null)
    {
        var folder = entry.FolderPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            AppLogger.Warn($"LaunchResume: folder not found '{folder}', falling back to user profile");
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        AppLogger.Info($"LaunchResume: sessionId={sessionId}, folder={folder}");
        var args = $"claude --resume {sessionId}{BuildModelArg(entry.ModelOverride)}{BuildEffortArg(effortOverride)}";
        LaunchInShell(folder, args, entry.ShellOverride);
    }

    // Cap on model override length — claude's longest documented full ID
    // (`claude-haiku-4-5-20251001`) is 25 chars; 64 is generous future-proofing
    // while still preventing pathological cmd-line bloat from a malicious
    // settings.json.
    private const int MaxModelOverrideLength = 64;

    /// <summary>
    /// Builds the <c>--model X</c> suffix (with leading space) when an override
    /// is set; otherwise returns empty. The override comes from the UI as a
    /// short alias (sonnet/opus/haiku) or a full model id (e.g. claude-fable-5)
    /// so we don't need to shell-escape — but we still validate here to keep
    /// arbitrary strings out
    /// of the command line if a user hand-edits settings.json. Allow-list is
    /// strictly ASCII alnum + <c>-_.</c>: everything claude's documented
    /// aliases and full IDs need, nothing that any of cmd/PowerShell/git-bash
    /// treat as a metacharacter, and no Unicode lookalikes (e.g. fullwidth
    /// Latin) that <see cref="char.IsLetterOrDigit(char)"/> would otherwise admit.
    /// </summary>
    internal static string BuildModelArg(string? modelOverride)
    {
        if (string.IsNullOrWhiteSpace(modelOverride))
        {
            return "";
        }

        var trimmed = modelOverride.Trim();

        if (trimmed.Length > MaxModelOverrideLength)
        {
            AppLogger.Warn($"BuildModelArg: dropping model override (length {trimmed.Length} exceeds {MaxModelOverrideLength})");
            return "";
        }

        foreach (var c in trimmed)
        {
            bool ok = (c >= 'a' && c <= 'z')
                   || (c >= 'A' && c <= 'Z')
                   || (c >= '0' && c <= '9')
                   ||  c == '-' || c == '_' || c == '.';
            if (!ok)
            {
                // Truncate the logged value so a malicious settings.json can't
                // flood the log file via a multi-MB override.
                var sample = trimmed.Length > 32 ? trimmed[..32] + "…" : trimmed;
                AppLogger.Warn($"BuildModelArg: dropping invalid model override '{sample}' (illegal character)");
                return "";
            }
        }

        return $" --model {trimmed}";
    }

    /// <summary>
    /// Builds the <c>--effort X</c> suffix (with leading space) when an
    /// override is set; otherwise returns empty. Unlike <see cref="BuildModelArg"/>
    /// the value is checked against a small allow-list — Anthropic's effort
    /// parameter only accepts a fixed enum and an unknown value would crash
    /// claude on startup. <c>ultracode</c> is intentionally excluded: it
    /// turns on automatic dynamic-workflow orchestration and isn't safe to
    /// expose as a global default in a tray app.
    /// </summary>
    internal static string BuildEffortArg(string? effortOverride)
    {
        if (string.IsNullOrWhiteSpace(effortOverride))
        {
            return "";
        }

        var trimmed = effortOverride.Trim().ToLowerInvariant();
        if (trimmed is not ("low" or "medium" or "high" or "xhigh" or "max"))
        {
            AppLogger.Warn($"BuildEffortArg: dropping unknown effort level '{trimmed}'");
            return "";
        }

        return $" --effort {trimmed}";
    }

    public static void LaunchWithPrompt(string folderPath, string prompt)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        System.Windows.Clipboard.SetText(prompt);
        LaunchInShell(folderPath, "claude");
    }

    private static void LaunchInShell(string workingDir, string claudeArgs, ShellType? shellOverride = null)
    {
        // Trailing backslash + surrounding "..." in shell args breaks the closing quote
        // (`"C:\foo\"` parses as one arg containing `"`), causing the next arg to fold
        // into the working dir. Strip trailing separators defensively here so older
        // settings.json entries (saved before the editor-side normalization) still work.
        workingDir = NormalizeWorkingDir(workingDir);

        var effective = shellOverride ?? PreferredShell;
        var shell = effective == ShellType.Auto ? DetectShell() : effective;

        // Git Bash has its own PATH and can't run .cmd files directly — don't substitute
        if (shell != ShellType.GitBash)
        {
            var resolvedClaude = ResolveClaudePath();
            if (resolvedClaude != null)
            {
                // Only replace the first occurrence (the command name), not session IDs
                var idx = claudeArgs.IndexOf("claude", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    claudeArgs = string.Concat(
                        claudeArgs.AsSpan(0, idx),
                        $"\"{resolvedClaude}\"",
                        claudeArgs.AsSpan(idx + "claude".Length));
                }
            }
        }

        AppLogger.Info($"LaunchInShell: shell={shell}, workingDir={workingDir}, claudeArgs={claudeArgs}");
        var wtPath = FindWindowsTerminal();

        switch (shell)
        {
            case ShellType.PowerShell:
                LaunchPowerShell(workingDir, claudeArgs, wtPath);
                break;
            case ShellType.GitBash:
                LaunchGitBash(workingDir, claudeArgs, wtPath);
                break;
            default:
                LaunchCmd(workingDir, claudeArgs, wtPath);
                break;
        }
    }

    private static void LaunchCmd(string workingDir, string claudeArgs, string? wtPath)
    {
        if (wtPath != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wtPath,
                Arguments = $"-d \"{workingDir}\" cmd /k {claudeArgs}",
                UseShellExecute = false
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k cd /d \"{workingDir}\" && {claudeArgs}",
                UseShellExecute = true
            });
        }
    }

    private static void LaunchPowerShell(string workingDir, string claudeArgs, string? wtPath)
    {
        var pwsh = FindPwsh() ?? "powershell.exe";

        if (wtPath != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wtPath,
                Arguments = $"-d \"{workingDir}\" \"{pwsh}\" -NoExit -Command \"& {claudeArgs}\"",
                UseShellExecute = false
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = pwsh,
                Arguments = $"-NoExit -Command \"Set-Location '{workingDir}'; & {claudeArgs}\"",
                UseShellExecute = true
            });
        }
    }

    private static void LaunchGitBash(string workingDir, string claudeArgs, string? wtPath)
    {
        var gitBash = FindGitBash();
        if (gitBash == null)
        {
            LaunchCmd(workingDir, claudeArgs, wtPath);
            return;
        }

        if (wtPath != null)
        {
            // Windows Terminal: run claude directly via bash -c. WT's "close on exit"
            // profile setting controls whether the tab stays open after claude exits.
            // Don't use "; exec bash" — WT splits on semicolons and opens a broken second tab.
            Process.Start(new ProcessStartInfo
            {
                FileName = wtPath,
                Arguments = $"-d \"{workingDir}\" \"{gitBash}\" --login -c \"{claudeArgs}\"",
                UseShellExecute = false
            });
        }
        else
        {
            // Without WT: bash stays open via -i, exec replaces the -c subshell
            // with an interactive bash after claude exits.
            Process.Start(new ProcessStartInfo
            {
                FileName = gitBash,
                Arguments = $"--cd=\"{workingDir}\" --login -i -c \"{claudeArgs}; exec bash\"",
                UseShellExecute = true
            });
        }
    }

    internal static string NormalizeWorkingDir(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // Strip every trailing separator while preserving roots (e.g. "C:\" stays).
        // TrimEndingDirectorySeparator only trims one at a time, so loop until stable.
        string previous;
        do
        {
            previous = path;
            path = Path.TrimEndingDirectorySeparator(path);
        } while (path != previous);

        return path;
    }

    internal static ShellType DetectShell()
    {
        if (ClaudeWorksIn("cmd.exe", "/c where claude"))
        {
            return ShellType.Cmd;
        }

        var pwsh = FindPwsh();
        if (pwsh != null && ClaudeWorksIn(pwsh, "-Command \"Get-Command claude -ErrorAction SilentlyContinue\""))
        {
            return ShellType.PowerShell;
        }

        var gitBash = FindGitBash();
        if (gitBash != null)
        {
            return ShellType.GitBash;
        }

        return ShellType.Cmd;
    }

    private static bool ClaudeWorksIn(string shell, string testArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = testArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null)
            {
                return false;
            }
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ResolveClaudePath()
    {
        if (!string.IsNullOrEmpty(ClaudeExePath) && File.Exists(ClaudeExePath))
        {
            return ClaudeExePath;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WinGet\Links\claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"npm\claude.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Anthropic\claude.exe"),
        };

        // Also scan WinGet packages directory for claude.exe
        var wingetPkgs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Packages");
        if (Directory.Exists(wingetPkgs))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(wingetPkgs, "Anthropic.ClaudeCode*"))
                {
                    var exe = Directory.GetFiles(dir, "claude.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (exe != null)
                    {
                        return exe;
                    }
                }
            }
            catch
            {
                // Ignore permission errors
            }
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string? FindWindowsTerminal()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var wtPath = Path.Combine(localAppData, @"Microsoft\WindowsApps\wt.exe");
        return File.Exists(wtPath) ? wtPath : null;
    }

    private static string? FindPwsh()
    {
        var candidates = new[]
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files (x86)\PowerShell\7\pwsh.exe",
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                return p;
            }
        }
        return File.Exists(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")
            ? "powershell.exe"
            : null;
    }

    private static string? FindGitBash()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Git\bin\bash.exe"),
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                return p;
            }
        }
        return null;
    }
}
