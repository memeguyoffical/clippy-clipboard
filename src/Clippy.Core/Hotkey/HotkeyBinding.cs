namespace Clippy.Core.Hotkey;

/// <summary>
/// A global shortcut: a set of modifier keys plus one virtual key. Kept free of
/// WinForms types so settings and tests can use it without a UI thread.
/// </summary>
public readonly record struct HotkeyBinding(uint Modifiers, int VirtualKey)
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;
    public const uint AllModifiers = Alt | Control | Shift | Win;

    /// <summary>Virtual key code for V, the default gesture.</summary>
    public const int DefaultKey = 0x56;

    public static HotkeyBinding Default => new(Win, DefaultKey);

    public bool UsesWindowsKey => (Modifiers & Win) != 0;

    /// <summary>True only for the shipped Win+V default.</summary>
    public bool IsWindowsHistoryGesture => this == Default;

    public static bool TryCreate(
        uint modifiers,
        int virtualKey,
        out HotkeyBinding binding,
        out string? error)
    {
        binding = default;
        error = null;
        modifiers &= AllModifiers;

        if (virtualKey == 0)
        {
            error = "Press a key after the modifiers.";
            return false;
        }

        if (IsModifierKey(virtualKey))
        {
            error = "That key is a modifier. Add a normal key too.";
            return false;
        }

        // A bare letter would eat normal typing everywhere. Function keys are
        // the exception: plenty of tools use F8/F9 on their own.
        if (modifiers == 0 && !IsFunctionKey(virtualKey))
        {
            error = "Hold Ctrl, Alt, Shift or Win, or use an F-key on its own.";
            return false;
        }

        if (modifiers == (Control | Alt) && virtualKey == 0x2E)
        {
            error = "Ctrl+Alt+Delete is reserved by Windows.";
            return false;
        }

        if (modifiers == Win && virtualKey == 0x4C)
        {
            error = "Win+L is reserved by Windows.";
            return false;
        }

        binding = new HotkeyBinding(modifiers, virtualKey);
        return true;
    }

    /// <summary>
    /// Accepts the same text <see cref="Describe"/> produces, plus common
    /// aliases such as "Control+Shift+V" or "Ctrl+Shift+Enter".
    /// </summary>
    public static bool TryParse(string? text, out HotkeyBinding binding, out string? error)
    {
        binding = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a hotkey.";
            return false;
        }

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = 0u;
        var virtualKey = 0;

        foreach (var token in tokens)
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Control;
                    continue;

                case "alt":
                case "option":
                    modifiers |= Alt;
                    continue;

                case "shift":
                    modifiers |= Shift;
                    continue;

                case "win":
                case "windows":
                case "super":
                case "meta":
                    modifiers |= Win;
                    continue;
            }

            if (virtualKey != 0)
            {
                error = "Only one normal key is allowed.";
                return false;
            }

            if (!TryParseKey(token, out virtualKey))
            {
                error = $"Unknown key \"{token}\".";
                return false;
            }
        }

        return TryCreate(modifiers, virtualKey, out binding, out error);
    }

    public string Describe()
    {
        var parts = new List<string>(5);

        if ((Modifiers & Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & Win) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(int virtualKey) => virtualKey
        is 0x10 or 0x11 or 0x12
        or 0x5B or 0x5C
        or 0xA0 or 0xA1
        or 0xA2 or 0xA3
        or 0xA4 or 0xA5;

    private static bool IsFunctionKey(int virtualKey) => virtualKey is >= 0x70 and <= 0x87;

    public static string KeyName(int virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return KnownKeys.TryGetValue(virtualKey, out var name)
            ? name
            : $"VK 0x{virtualKey:X2}";
    }

    private static bool TryParseKey(string token, out int virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);

            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }

            if (PunctuationKeys.TryGetValue(token[0], out virtualKey))
            {
                return true;
            }
        }

        if (token.Length is >= 2 and <= 3 && (token[0] is 'f' or 'F'))
        {
            if (int.TryParse(token.AsSpan(1), out var number) && number is >= 1 and <= 24)
            {
                virtualKey = 0x6F + number;
                return true;
            }
        }

        if (KeyAliases.TryGetValue(token, out virtualKey))
        {
            return true;
        }

        return false;
    }

    private static readonly Dictionary<int, string> KnownKeys = new()
    {
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Pause",
        [0x14] = "CapsLock",
        [0x1B] = "Esc",
        [0x20] = "Space",
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Left",
        [0x26] = "Up",
        [0x27] = "Right",
        [0x28] = "Down",
        [0x2C] = "PrintScreen",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0x5D] = "Menu",
        [0x60] = "Num 0",
        [0x61] = "Num 1",
        [0x62] = "Num 2",
        [0x63] = "Num 3",
        [0x64] = "Num 4",
        [0x65] = "Num 5",
        [0x66] = "Num 6",
        [0x67] = "Num 7",
        [0x68] = "Num 8",
        [0x69] = "Num 9",
        [0x6A] = "Num *",
        [0x6B] = "Num +",
        [0x6D] = "Num -",
        [0x6E] = "Num .",
        [0x6F] = "Num /",
        [0x90] = "NumLock",
        [0x91] = "ScrollLock",
        [0xBA] = ";",
        [0xBB] = "=",
        [0xBC] = ",",
        [0xBD] = "-",
        [0xBE] = ".",
        [0xBF] = "/",
        [0xC0] = "`",
        [0xDB] = "[",
        [0xDC] = "\\",
        [0xDD] = "]",
        [0xDE] = "'",
    };

    private static readonly Dictionary<string, int> KeyAliases = BuildKeyAliases();

    private static readonly Dictionary<char, int> PunctuationKeys = new()
    {
        [';'] = 0xBA,
        ['='] = 0xBB,
        [','] = 0xBC,
        ['-'] = 0xBD,
        ['.'] = 0xBE,
        ['/'] = 0xBF,
        ['`'] = 0xC0,
        ['['] = 0xDB,
        ['\\'] = 0xDC,
        [']'] = 0xDD,
        ['\''] = 0xDE,
    };

    private static Dictionary<string, int> BuildKeyAliases()
    {
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08,
            ["back"] = 0x08,
            ["tab"] = 0x09,
            ["enter"] = 0x0D,
            ["return"] = 0x0D,
            ["pause"] = 0x13,
            ["capslock"] = 0x14,
            ["caps"] = 0x14,
            ["esc"] = 0x1B,
            ["escape"] = 0x1B,
            ["space"] = 0x20,
            ["pageup"] = 0x21,
            ["pgup"] = 0x21,
            ["pagedown"] = 0x22,
            ["pgdn"] = 0x22,
            ["end"] = 0x23,
            ["home"] = 0x24,
            ["left"] = 0x25,
            ["up"] = 0x26,
            ["right"] = 0x27,
            ["down"] = 0x28,
            ["printscreen"] = 0x2C,
            ["insert"] = 0x2D,
            ["ins"] = 0x2D,
            ["delete"] = 0x2E,
            ["del"] = 0x2E,
            ["menu"] = 0x5D,
            ["num*"] = 0x6A,
            ["num+"] = 0x6B,
            ["num-"] = 0x6D,
            ["num."] = 0x6E,
            ["num/"] = 0x6F,
            ["numlock"] = 0x90,
            ["scrolllock"] = 0x91,
            ["semicolon"] = 0xBA,
            ["equals"] = 0xBB,
            ["comma"] = 0xBC,
            ["minus"] = 0xBD,
            ["period"] = 0xBE,
            ["slash"] = 0xBF,
            ["backtick"] = 0xC0,
            ["tilde"] = 0xC0,
            ["openbracket"] = 0xDB,
            ["leftbracket"] = 0xDB,
            ["backslash"] = 0xDC,
            ["closebracket"] = 0xDD,
            ["rightbracket"] = 0xDD,
            ["quote"] = 0xDE,
        };

        for (var digit = 0; digit <= 9; digit++)
        {
            aliases[$"num{digit}"] = 0x60 + digit;
            aliases[$"numpad{digit}"] = 0x60 + digit;
        }

        return aliases;
    }
}
