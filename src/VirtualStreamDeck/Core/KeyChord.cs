namespace VirtualStreamDeck.Core;

[Flags]
public enum ChordModifiers
{
    None = 0,
    Alt = 1,     // same bit values as RegisterHotKey's MOD_* flags
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A key combination such as "CTRL + SHIFT + M". Parsing is forgiving (case, spacing, aliases);
/// formatting is canonical so two spellings of the same chord compare equal.
/// </summary>
public readonly record struct KeyChord(ChordModifiers Modifiers, int VirtualKey)
{
    public bool IsEmpty => Modifiers == ChordModifiers.None && VirtualKey == 0;
    public bool HasKey => VirtualKey != 0;

    private static readonly Dictionary<string, int> NameToVk = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> VkToName = new();

    static KeyChord()
    {
        void Add(int vk, params string[] names)
        {
            foreach (var n in names) NameToVk[n] = vk;
            VkToName.TryAdd(vk, names[0]);
        }

        for (var c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
        for (var d = 0; d <= 9; d++) Add('0' + d, d.ToString());
        for (var f = 1; f <= 24; f++) Add(0x6F + f, $"F{f}");
        for (var n = 0; n <= 9; n++) Add(0x60 + n, $"NUM{n}", $"NUMPAD{n}");

        Add(0x08, "BACKSPACE", "BACK", "BKSP");
        Add(0x09, "TAB");
        Add(0x0D, "ENTER", "RETURN");
        Add(0x13, "PAUSE", "BREAK");
        Add(0x14, "CAPSLOCK", "CAPS");
        Add(0x1B, "ESC", "ESCAPE");
        Add(0x20, "SPACE", "SPACEBAR");
        Add(0x21, "PAGEUP", "PGUP", "PRIOR");
        Add(0x22, "PAGEDOWN", "PGDN", "NEXT");
        Add(0x23, "END");
        Add(0x24, "HOME");
        Add(0x25, "LEFT");
        Add(0x26, "UP");
        Add(0x27, "RIGHT");
        Add(0x28, "DOWN");
        Add(0x2C, "PRINTSCREEN", "PRTSC", "PRINT", "SNAPSHOT");
        Add(0x2D, "INSERT", "INS");
        Add(0x2E, "DELETE", "DEL");
        Add(0x5D, "MENU", "APPS", "CONTEXT");
        Add(0x6A, "MULTIPLY", "NUM*");
        Add(0x6B, "ADD", "NUM+");
        Add(0x6D, "SUBTRACT", "NUM-");
        Add(0x6E, "DECIMAL", "NUM.");
        Add(0x6F, "DIVIDE", "NUM/");
        Add(0x90, "NUMLOCK");
        Add(0x91, "SCROLLLOCK", "SCROLL");
        Add(0xA0, "LSHIFT");
        Add(0xA1, "RSHIFT");
        Add(0xA2, "LCTRL");
        Add(0xA3, "RCTRL");
        Add(0xA4, "LALT");
        Add(0xA5, "RALT", "ALTGR");
        Add(0x5B, "LWIN");
        Add(0x5C, "RWIN");
        Add(0xAD, "VOLUMEMUTE", "MUTE");
        Add(0xAE, "VOLUMEDOWN", "VOLDOWN");
        Add(0xAF, "VOLUMEUP", "VOLUP");
        Add(0xB0, "MEDIANEXT", "NEXTTRACK");
        Add(0xB1, "MEDIAPREV", "PREVTRACK");
        Add(0xB2, "MEDIASTOP");
        Add(0xB3, "MEDIAPLAYPAUSE", "PLAYPAUSE");
        Add(0xA6, "BROWSERBACK");
        Add(0xA7, "BROWSERFORWARD");
        Add(0xA8, "BROWSERREFRESH");
        Add(0xAC, "BROWSERHOME");
        Add(0xBA, ";", "SEMICOLON");
        Add(0xBB, "=", "EQUALS", "PLUS");
        Add(0xBC, ",", "COMMA");
        Add(0xBD, "-", "MINUS");
        Add(0xBE, ".", "PERIOD");
        Add(0xBF, "/", "SLASH");
        Add(0xC0, "`", "GRAVE", "TILDE");
        Add(0xDB, "[", "LBRACKET");
        Add(0xDC, "\\", "BACKSLASH");
        Add(0xDD, "]", "RBRACKET");
        Add(0xDE, "'", "QUOTE", "APOSTROPHE");
    }

    public static IReadOnlyCollection<string> KeyNames => VkToName.Values;

    public static bool IsModifierVk(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    public static string? NameOf(int vk) => VkToName.TryGetValue(vk, out var n) ? n : null;
    public static int VkOf(string name) => NameToVk.TryGetValue(name.Trim(), out var vk) ? vk : 0;

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ChordModifiers.None;
        var vk = 0;
        // Split on '+' but allow the key itself to be "+" written as "PLUS"; a trailing "+ +" is also accepted.
        var raw = text.Replace(" ", "");
        var parts = new List<string>();
        var cur = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (ch == '+' && cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());

        foreach (var p in parts)
        {
            switch (p.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": case "CTL": mods |= ChordModifiers.Ctrl; continue;
                case "ALT": case "MENU_ALT": mods |= ChordModifiers.Alt; continue;
                case "SHIFT": mods |= ChordModifiers.Shift; continue;
                case "WIN": case "WINDOWS": case "META": case "SUPER": case "CMD": mods |= ChordModifiers.Win; continue;
            }
            if (vk != 0) return false; // two non-modifier keys
            vk = VkOf(p);
            if (vk == 0) return false;
        }
        if (mods == ChordModifiers.None && vk == 0) return false;
        chord = new KeyChord(mods, vk);
        return true;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"Not a valid key combination: '{text}'");

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ChordModifiers.Ctrl)) parts.Add("CTRL");
        if (Modifiers.HasFlag(ChordModifiers.Alt)) parts.Add("ALT");
        if (Modifiers.HasFlag(ChordModifiers.Shift)) parts.Add("SHIFT");
        if (Modifiers.HasFlag(ChordModifiers.Win)) parts.Add("WIN");
        if (VirtualKey != 0) parts.Add(NameOf(VirtualKey) ?? $"VK{VirtualKey:X2}");
        return string.Join(" + ", parts);
    }

    /// <summary>Returns the canonical spelling, or the input unchanged when it does not parse.</summary>
    public static string Normalize(string? text) => TryParse(text, out var c) ? c.ToString() : text?.Trim() ?? "";

    /// <summary>Keys that need KEYEVENTF_EXTENDEDKEY when injected with SendInput.</summary>
    public static bool IsExtended(int vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E
        or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5
        or >= 0xA6 and <= 0xB7;
}
