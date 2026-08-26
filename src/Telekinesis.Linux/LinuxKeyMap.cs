namespace Telekinesis.Linux;

/// <summary>
/// Linux input-event key codes (from linux/input-event-codes.h) and the
/// US-layout character mapping used to type text through uinput.
/// </summary>
internal static class LinuxKeyMap
{
    // A subset of KEY_* codes, enough for text entry and common chords.
    public const int KEY_ESC = 1, KEY_1 = 2, KEY_2 = 3, KEY_3 = 4, KEY_4 = 5, KEY_5 = 6,
        KEY_6 = 7, KEY_7 = 8, KEY_8 = 9, KEY_9 = 10, KEY_0 = 11, KEY_MINUS = 12, KEY_EQUAL = 13,
        KEY_BACKSPACE = 14, KEY_TAB = 15, KEY_Q = 16, KEY_W = 17, KEY_E = 18, KEY_R = 19,
        KEY_T = 20, KEY_Y = 21, KEY_U = 22, KEY_I = 23, KEY_O = 24, KEY_P = 25,
        KEY_LEFTBRACE = 26, KEY_RIGHTBRACE = 27, KEY_ENTER = 28, KEY_LEFTCTRL = 29,
        KEY_A = 30, KEY_S = 31, KEY_D = 32, KEY_F = 33, KEY_G = 34, KEY_H = 35, KEY_J = 36,
        KEY_K = 37, KEY_L = 38, KEY_SEMICOLON = 39, KEY_APOSTROPHE = 40, KEY_GRAVE = 41,
        KEY_LEFTSHIFT = 42, KEY_BACKSLASH = 43, KEY_Z = 44, KEY_X = 45, KEY_C = 46, KEY_V = 47,
        KEY_B = 48, KEY_N = 49, KEY_M = 50, KEY_COMMA = 51, KEY_DOT = 52, KEY_SLASH = 53,
        KEY_RIGHTSHIFT = 54, KEY_LEFTALT = 56, KEY_SPACE = 57, KEY_CAPSLOCK = 58,
        KEY_F1 = 59, KEY_F2 = 60, KEY_F3 = 61, KEY_F4 = 62, KEY_F5 = 63, KEY_F6 = 64,
        KEY_F7 = 65, KEY_F8 = 66, KEY_F9 = 67, KEY_F10 = 68, KEY_F11 = 87, KEY_F12 = 88,
        KEY_HOME = 102, KEY_UP = 103, KEY_PAGEUP = 104, KEY_LEFT = 105, KEY_RIGHT = 106,
        KEY_END = 107, KEY_DOWN = 108, KEY_PAGEDOWN = 109, KEY_INSERT = 110, KEY_DELETE = 111,
        KEY_LEFTMETA = 125;

    // Mouse buttons live in the same EV_KEY space.
    public const int BTN_LEFT = 0x110, BTN_RIGHT = 0x111, BTN_MIDDLE = 0x112;

    /// <summary>Named keys usable in chords (press_keys), lower-cased.</summary>
    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = KEY_LEFTCTRL, ["control"] = KEY_LEFTCTRL,
        ["shift"] = KEY_LEFTSHIFT, ["alt"] = KEY_LEFTALT,
        ["meta"] = KEY_LEFTMETA, ["super"] = KEY_LEFTMETA, ["win"] = KEY_LEFTMETA,
        ["enter"] = KEY_ENTER, ["return"] = KEY_ENTER, ["esc"] = KEY_ESC, ["escape"] = KEY_ESC,
        ["tab"] = KEY_TAB, ["space"] = KEY_SPACE, ["backspace"] = KEY_BACKSPACE, ["delete"] = KEY_DELETE,
        ["del"] = KEY_DELETE, ["insert"] = KEY_INSERT, ["home"] = KEY_HOME, ["end"] = KEY_END,
        ["pageup"] = KEY_PAGEUP, ["pagedown"] = KEY_PAGEDOWN,
        ["up"] = KEY_UP, ["down"] = KEY_DOWN, ["left"] = KEY_LEFT, ["right"] = KEY_RIGHT,
        ["f1"] = KEY_F1, ["f2"] = KEY_F2, ["f3"] = KEY_F3, ["f4"] = KEY_F4, ["f5"] = KEY_F5,
        ["f6"] = KEY_F6, ["f7"] = KEY_F7, ["f8"] = KEY_F8, ["f9"] = KEY_F9, ["f10"] = KEY_F10,
        ["f11"] = KEY_F11, ["f12"] = KEY_F12,
    };

    public static bool TryNamedKey(string name, out int code) => Named.TryGetValue(name.Trim(), out code);

    /// <summary>Maps a character to its US-layout key code and whether Shift is required.</summary>
    public static bool TryChar(char c, out int code, out bool shift)
    {
        shift = false;
        if (c is >= 'a' and <= 'z') { code = KEY_A + (c - 'a'); return true; }
        if (c is >= 'A' and <= 'Z') { code = KEY_A + (c - 'A'); shift = true; return true; }
        if (c is >= '1' and <= '9') { code = KEY_1 + (c - '1'); return true; }

        (int Code, bool Shift) m = c switch
        {
            '0' => (KEY_0, false),
            ' ' => (KEY_SPACE, false),
            '\n' => (KEY_ENTER, false),
            '\t' => (KEY_TAB, false),
            '-' => (KEY_MINUS, false), '_' => (KEY_MINUS, true),
            '=' => (KEY_EQUAL, false), '+' => (KEY_EQUAL, true),
            '[' => (KEY_LEFTBRACE, false), '{' => (KEY_LEFTBRACE, true),
            ']' => (KEY_RIGHTBRACE, false), '}' => (KEY_RIGHTBRACE, true),
            ';' => (KEY_SEMICOLON, false), ':' => (KEY_SEMICOLON, true),
            '\'' => (KEY_APOSTROPHE, false), '"' => (KEY_APOSTROPHE, true),
            '`' => (KEY_GRAVE, false), '~' => (KEY_GRAVE, true),
            '\\' => (KEY_BACKSLASH, false), '|' => (KEY_BACKSLASH, true),
            ',' => (KEY_COMMA, false), '<' => (KEY_COMMA, true),
            '.' => (KEY_DOT, false), '>' => (KEY_DOT, true),
            '/' => (KEY_SLASH, false), '?' => (KEY_SLASH, true),
            '!' => (KEY_1, true), '@' => (KEY_2, true), '#' => (KEY_3, true), '$' => (KEY_4, true),
            '%' => (KEY_5, true), '^' => (KEY_6, true), '&' => (KEY_7, true), '*' => (KEY_8, true),
            '(' => (KEY_9, true), ')' => (KEY_0, true),
            _ => (0, false),
        };
        code = m.Code; shift = m.Shift;
        return code != 0;
    }
}
