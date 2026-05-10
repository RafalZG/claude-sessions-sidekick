namespace ClaudeSessionsSidekick.Services;

/// <summary>
/// Hotkey string parsing and UI helpers. Parsing converts strings like
/// "Win+Alt+S" or "Ctrl+Alt+1" into ModState flags + virtual key code.
/// Plain "Ctrl"/"Alt"/"Shift" map to Left variants only, preventing
/// AltGr (Right Alt = Ctrl+Alt on European keyboards) from triggering shortcuts.
/// </summary>
public static class HotkeyHelper
{
    /// <summary>
    /// Modifier entries for Settings UI dropdowns.
    /// Each tuple: (display name for UI, value for hotkey string).
    /// </summary>
    public static readonly (string Display, string Value)[] Modifiers =
    [
        ("Ctrl (Left)", "LCtrl"),
        ("Ctrl (Right)", "RCtrl"),
        ("Alt (Left)", "LAlt"),
        ("Alt (Right)", "RAlt"),
        ("Shift (Left)", "LShift"),
        ("Shift (Right)", "RShift"),
        ("Win", "Win"),
    ];

    public static readonly string[] KeyNames = [
        "A","B","C","D","E","F","G","H","I","J","K","L","M",
        "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
        "0","1","2","3","4","5","6","7","8","9",
        "Num0","Num1","Num2","Num3","Num4","Num5","Num6","Num7","Num8","Num9",
        "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12"
    ];

    public static bool TryParse(string hotkeyString, out ModState modifiers, out uint vk)
    {
        modifiers = ModState.None;
        vk = 0;

        if (string.IsNullOrWhiteSpace(hotkeyString))
        {
            return false;
        }

        var parts = hotkeyString.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        // Last part is the key, everything before is modifiers
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var mod = ModifierToFlag(parts[i]);
            if (mod == ModState.None)
            {
                return false;
            }
            modifiers |= mod;
        }

        vk = KeyToVirtualKey(parts[^1]);
        return vk != 0;
    }

    public static string Build(string mod1, string? mod2, string key)
    {
        if (string.IsNullOrEmpty(mod2))
        {
            return $"{mod1}+{key}";
        }
        return $"{mod1}+{mod2}+{key}";
    }

    /// <summary>
    /// Maps a saved value (e.g. "Alt", "LAlt") to the UI display name ("Alt (Left)").
    /// Handles backward compat: plain "Ctrl"/"Alt"/"Shift" map to Left variants.
    /// </summary>
    public static string ValueToDisplay(string value)
    {
        var normalized = value.ToUpperInvariant() switch
        {
            "CTRL" or "LCTRL" => "LCtrl",
            "RCTRL" => "RCtrl",
            "ALT" or "LALT" => "LAlt",
            "RALT" => "RAlt",
            "SHIFT" or "LSHIFT" => "LShift",
            "RSHIFT" => "RShift",
            "WIN" => "Win",
            _ => value
        };

        foreach (var (display, val) in Modifiers)
        {
            if (val.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }
        return value;
    }

    /// <summary>
    /// Maps a UI display name ("Alt (Left)") to the compact value ("LAlt") for saving.
    /// </summary>
    public static string DisplayToValue(string display)
    {
        foreach (var (d, val) in Modifiers)
        {
            if (d == display)
            {
                return val;
            }
        }
        return display;
    }

    internal static ModState ModifierToFlag(string modifier)
    {
        return modifier.ToUpperInvariant() switch
        {
            "CTRL" or "LCTRL" => ModState.LCtrl,
            "RCTRL" => ModState.RCtrl,
            "ALT" or "LALT" => ModState.LAlt,
            "RALT" => ModState.RAlt,
            "SHIFT" or "LSHIFT" => ModState.LShift,
            "RSHIFT" => ModState.RShift,
            "WIN" => ModState.Win,
            _ => ModState.None
        };
    }

    internal static uint KeyToVirtualKey(string key)
    {
        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                return ch;
            }
            if (ch is >= '0' and <= '9')
            {
                return ch;
            }
        }

        // Numpad keys: Num0-Num9 → VK_NUMPAD0 (0x60) - VK_NUMPAD9 (0x69)
        if (key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
            key.Length == 4 &&
            key[3] is >= '0' and <= '9')
        {
            return (uint)(0x60 + (key[3] - '0'));
        }

        // Function keys
        if (key.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key.AsSpan(1), out var fNum) &&
            fNum is >= 1 and <= 12)
        {
            return (uint)(0x70 + fNum - 1); // VK_F1 = 0x70
        }

        return 0;
    }
}
