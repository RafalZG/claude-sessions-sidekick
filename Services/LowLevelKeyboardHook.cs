using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Global keyboard hook using WH_KEYBOARD_LL that distinguishes Left/Right
/// modifier keys. Replaces RegisterHotKey which treats AltGr (Right Alt) as
/// Ctrl+Alt, breaking @ on European keyboards.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Left/Right virtual key codes
    private const uint VK_LCONTROL = 0xA2;
    private const uint VK_RCONTROL = 0xA3;
    private const uint VK_LMENU = 0xA4;   // Left Alt
    private const uint VK_RMENU = 0xA5;   // Right Alt / AltGr
    private const uint VK_LSHIFT = 0xA0;
    private const uint VK_RSHIFT = 0xA1;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private IntPtr _hookHandle;
    private readonly LowLevelKeyboardProc _hookProc; // prevent GC
    private ModState _modifiers;

    private readonly List<HotkeyBinding> _bindings = [];
    private int _nextId = 1;
    private readonly object _lock = new();

    // Debounce: prevent the same hotkey from firing multiple times
    // due to key-repeat messages from held keys
    private int _lastTriggeredId;
    private uint _lastTriggeredVk;

    public LowLevelKeyboardHook()
    {
        _hookProc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            AppLogger.Warn($"Failed to install keyboard hook, error: {Marshal.GetLastWin32Error()}");
        }
    }

    public int Register(string hotkeyString, Action callback)
    {
        if (!HotkeyHelper.TryParse(hotkeyString, out var mods, out var vk))
        {
            return -1;
        }

        lock (_lock)
        {
            var id = _nextId++;
            _bindings.Add(new HotkeyBinding(id, mods, vk, callback));
            return id;
        }
    }

    public void Unregister(int id)
    {
        if (id < 0)
        {
            return;
        }

        lock (_lock)
        {
            _bindings.RemoveAll(b => b.Id == id);
        }
    }

    public void UnregisterAll()
    {
        lock (_lock)
        {
            _bindings.Clear();
        }
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Resets modifier tracking — call on session lock/unlock to prevent
    /// stuck modifiers when keys are released while screen is locked.
    /// </summary>
    public void ResetState()
    {
        _modifiers = ModState.None;
        _lastTriggeredId = 0;
        _lastTriggeredVk = 0;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();
            var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var isUp = msg is WM_KEYUP or WM_SYSKEYUP;

            if (isDown || isUp)
            {
                if (UpdateModifierState(kbd.vkCode, isDown, kbd.flags))
                {
                    // Was a modifier key — don't check bindings
                }
                else if (isDown)
                {
                    CheckBindings(kbd.vkCode);
                }
                else
                {
                    // Key up for a non-modifier — clear debounce
                    if (kbd.vkCode == _lastTriggeredVk)
                    {
                        _lastTriggeredId = 0;
                        _lastTriggeredVk = 0;
                    }
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// Updates modifier state. Returns true if the key was a modifier.
    /// </summary>
    private bool UpdateModifierState(uint vk, bool isDown, uint flags)
    {
        var flag = vk switch
        {
            VK_LCONTROL => ModState.LCtrl,
            VK_RCONTROL => ModState.RCtrl,
            VK_LSHIFT => ModState.LShift,
            VK_RSHIFT => ModState.RShift,
            VK_LMENU => ModState.LAlt,
            VK_RMENU => ModState.RAlt,
            VK_LWIN or VK_RWIN => ModState.Win,
            _ => ModState.None
        };

        if (flag == ModState.None)
        {
            return false;
        }

        if (isDown)
        {
            // AltGr handling: Windows sends a phantom VK_LCONTROL before VK_RMENU.
            // When Right Alt goes down, if LCtrl was just set, it's the phantom — clear it.
            if (vk == VK_RMENU)
            {
                _modifiers &= ~ModState.LCtrl; // remove phantom LCtrl from AltGr
            }

            _modifiers |= flag;
        }
        else
        {
            _modifiers &= ~flag;
        }

        return true;
    }

    /// <summary>
    /// Reads the actual modifier key state from the OS, not from our tracked state.
    /// Prevents stuck-modifier bugs where key-up events are missed (e.g. after
    /// focus change, Win key handled by shell, or hotkey opening a new window).
    /// </summary>
    private static ModState GetActualModifierState()
    {
        var state = ModState.None;
        if ((GetAsyncKeyState(0xA2) & 0x8000) != 0) state |= ModState.LCtrl;
        if ((GetAsyncKeyState(0xA3) & 0x8000) != 0) state |= ModState.RCtrl;
        if ((GetAsyncKeyState(0xA0) & 0x8000) != 0) state |= ModState.LShift;
        if ((GetAsyncKeyState(0xA1) & 0x8000) != 0) state |= ModState.RShift;
        if ((GetAsyncKeyState(0xA4) & 0x8000) != 0) state |= ModState.LAlt;
        if ((GetAsyncKeyState(0xA5) & 0x8000) != 0) state |= ModState.RAlt;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) state |= ModState.Win;
        return state;
    }

    private void CheckBindings(uint vk)
    {
        // Use actual OS key state instead of tracked state to prevent stuck modifiers.
        // Tracked state can get stale when key-up events are missed (focus change,
        // Win key handled by shell, hotkey opening a new window).
        var actualModifiers = GetActualModifierState();
        _modifiers = actualModifiers; // sync tracked state

        lock (_lock)
        {
            foreach (var binding in _bindings)
            {
                if (binding.VirtualKey != vk)
                {
                    continue;
                }

                // Exact modifier match using real-time OS state
                if (actualModifiers != binding.RequiredModifiers)
                {
                    continue;
                }

                // Debounce: don't re-fire on key repeat
                if (_lastTriggeredId == binding.Id && _lastTriggeredVk == vk)
                {
                    return;
                }

                _lastTriggeredId = binding.Id;
                _lastTriggeredVk = vk;

                var callback = binding.Callback;
                // Dispatch to UI thread — hook callback must return quickly
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(callback);
                return;
            }
        }
    }

    private record struct HotkeyBinding(int Id, ModState RequiredModifiers, uint VirtualKey, Action Callback);
}

/// <summary>
/// Modifier state flags with Left/Right distinction.
/// </summary>
[Flags]
public enum ModState : byte
{
    None   = 0,
    LCtrl  = 1 << 0,
    RCtrl  = 1 << 1,
    LShift = 1 << 2,
    RShift = 1 << 3,
    LAlt   = 1 << 4,
    RAlt   = 1 << 5,
    Win    = 1 << 6,
}
