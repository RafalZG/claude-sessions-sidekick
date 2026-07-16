using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using ClaudeSessionsSidekick.Models;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Turns a clipboard screenshot into something Claude Code can look at, without
/// the user saving a file by hand and typing its path.
///
/// Why it works the way it does: on native Windows terminals (Windows Terminal,
/// cmd, PowerShell console) there is NO reliable way to paste an *image* into
/// Claude Code — neither a raw clipboard bitmap (DIB/BMP isn't decoded, see
/// anthropics/claude-code#26679) nor a file-drop (the terminal's Ctrl+V pastes
/// only the clipboard's *text*, so a CF_HDROP with no text yields a stray glyph).
/// Validated empirically 2026-07-16: both approaches failed on Windows Terminal.
///
/// But Claude Code doesn't need an attached image — given a file PATH it reads
/// the file itself with its Read tool. So the reliable mechanism is simply to
/// hand it the path as text:
///
///   1. take the bitmap sitting on the clipboard,
///   2. write it to disk as a real PNG,
///   3. put that PNG's PATH on the clipboard as TEXT,
///   4. synthesize Ctrl+V into the focused terminal.
///
/// From the user's point of view: screenshot to clipboard, press the hotkey, the
/// path drops into the prompt; ask your question and Claude reads the screenshot.
/// (Saving the clipboard bitmap as a PNG the user could have copied by hand is
/// the same reason a plain path works — Claude reads a real file off disk.)
/// </summary>
public static class ScreenshotPasteService
{
    public enum PasteResult
    {
        Success,
        NoImage,
        ClipboardBusy,
        SaveFailed,
    }

    /// <summary>
    /// Must be called on an STA thread (the WPF UI thread). Callers dispatch to
    /// it via the keyboard hook's UI-thread BeginInvoke, so this is satisfied.
    /// </summary>
    public static PasteResult PasteFromClipboard(AppSettings settings)
    {
        if (!ContainsImageWithRetry(out var hasImage))
        {
            return PasteResult.ClipboardBusy;
        }

        if (!hasImage)
        {
            return PasteResult.NoImage;
        }

        BitmapSource? image = GetImageWithRetry();
        if (image is null)
        {
            return PasteResult.ClipboardBusy;
        }

        string path;
        try
        {
            var dir = ResolveSaveDir(settings);
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"shot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(fs);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ScreenshotPaste: failed to save PNG", ex);
            return PasteResult.SaveFailed;
        }

        // Type the path straight into the prompt. A path with spaces is quoted so
        // Claude Code (and any shell that might see it) treats it as one token. A
        // trailing space lets the user start typing their question immediately after.
        var promptText = (path.Contains(' ') ? $"\"{path}\"" : path) + " ";
        TypeText(promptText);
        CleanupOldShots(settings);
        return PasteResult.Success;
    }

    internal static string ResolveSaveDir(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ScreenshotSaveDir))
        {
            return settings.ScreenshotSaveDir!;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeSessionsSidekick",
            "Screenshots");
    }

    /// <summary>
    /// Keeps only the newest N PNGs so repeated pastes don't pile up forever.
    /// A retention of 0 or less means "keep everything".
    /// </summary>
    internal static void CleanupOldShots(AppSettings settings)
    {
        var keep = settings.ScreenshotRetentionCount;
        if (keep <= 0)
        {
            return;
        }

        try
        {
            var dir = ResolveSaveDir(settings);
            var files = new DirectoryInfo(dir).GetFiles("shot_*.png");
            if (files.Length <= keep)
            {
                return;
            }

            foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep))
            {
                try { f.Delete(); }
                catch (Exception ex) { AppLogger.Warn($"ScreenshotPaste: could not delete {f.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"ScreenshotPaste: cleanup skipped: {ex.Message}");
        }
    }

    // ---- Clipboard helpers (retried: the clipboard is a shared, lockable resource) ----

    private static bool ContainsImageWithRetry(out bool hasImage)
    {
        hasImage = false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                hasImage = Clipboard.ContainsImage();
                return true;
            }
            catch (Exception ex) when (attempt < 4)
            {
                AppLogger.Warn($"ScreenshotPaste: ContainsImage retry {attempt + 1}: {ex.Message}");
                Thread.Sleep(50);
            }
        }
        return false;
    }

    private static BitmapSource? GetImageWithRetry()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return Clipboard.GetImage();
            }
            catch (Exception ex) when (attempt < 4)
            {
                AppLogger.Warn($"ScreenshotPaste: GetImage retry {attempt + 1}: {ex.Message}");
                Thread.Sleep(50);
            }
        }
        return null;
    }

    // ---- Type the path straight into the focused (terminal) window ----

    // We don't synthesize Ctrl+V: a synthetic paste is fragile on Windows
    // Terminal (it pastes only clipboard *text*, and the trigger hotkey's
    // physically-held Ctrl/Alt — as specific L/R vk codes — can make the
    // injected V read as Alt+V instead of a paste). Instead we release every
    // modifier the trigger might be holding, then emit each character as a
    // KEYEVENTF_UNICODE event, which the terminal receives as plain text input
    // regardless of keyboard layout or paste bindings. Bonus: the clipboard is
    // left untouched. Injected events carry LLKHF_INJECTED, so our own
    // LowLevelKeyboardHook ignores them and won't re-fire the hotkey.
    private static void TypeText(string text)
    {
        // Let key-repeat of the trigger drain and give the user a beat to release.
        Thread.Sleep(60);

        var inputs = new List<INPUT>
        {
            // Release both L/R variants of every modifier the hotkey may hold,
            // so the typed characters aren't swallowed as chords.
            KeyUp(VK_LCONTROL), KeyUp(VK_RCONTROL),
            KeyUp(VK_LMENU),    KeyUp(VK_RMENU),
            KeyUp(VK_LSHIFT),   KeyUp(VK_RSHIFT),
            KeyUp(VK_LWIN),     KeyUp(VK_RWIN),
        };

        foreach (var ch in text)
        {
            inputs.Add(UnicodeDown(ch));
            inputs.Add(UnicodeUp(ch));
        }

        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length)
        {
            AppLogger.Warn($"ScreenshotPaste: SendInput sent {sent}/{arr.Length} events (err {Marshal.GetLastWin32Error()}). " +
                           "If the terminal runs elevated and the app does not, UIPI blocks synthetic input.");
        }
        else
        {
            AppLogger.Info($"ScreenshotPaste: typed {text.Length}-char path into the focused window");
        }
    }

    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;   // Left Alt
    private const ushort VK_RMENU = 0xA5;   // Right Alt / AltGr
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private static INPUT KeyUp(ushort vk) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    // Unicode input: wVk must be 0, the character goes in wScan, and
    // KEYEVENTF_UNICODE tells Windows to deliver it as a literal character
    // (a WM_CHAR) rather than translating a virtual key through the layout.
    private static INPUT UnicodeDown(char ch) => MakeUnicode(ch, KEYEVENTF_UNICODE);
    private static INPUT UnicodeUp(char ch) => MakeUnicode(ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);

    private static INPUT MakeUnicode(char ch, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    // The union must be laid out over the largest member (MOUSEINPUT) so that
    // Marshal.SizeOf<INPUT> matches the OS's expected sizeof(INPUT) — 40 bytes on
    // x64, 28 on x86. Passing the wrong cbSize makes SendInput silently do nothing.
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
