using System.Runtime.InteropServices;

namespace Telekinesis.Windows;

/// <summary>
/// Windows virtual-key codes for chords (press_keys). Named keys accept the same
/// spellings as the Linux backend so agent-facing behavior is identical; single
/// characters resolve through the active keyboard layout via VkKeyScanW.
/// </summary>
internal static class WindowsKeyMap
{
    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B,
        VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_TAB = 0x09, VK_SPACE = 0x20, VK_BACK = 0x08,
        VK_DELETE = 0x2E, VK_INSERT = 0x2D, VK_HOME = 0x24, VK_END = 0x23,
        VK_PRIOR = 0x21, VK_NEXT = 0x22,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VK_CONTROL, ["control"] = VK_CONTROL,
        ["shift"] = VK_SHIFT, ["alt"] = VK_MENU,
        ["meta"] = VK_LWIN, ["super"] = VK_LWIN, ["win"] = VK_LWIN,
        ["enter"] = VK_RETURN, ["return"] = VK_RETURN, ["esc"] = VK_ESCAPE, ["escape"] = VK_ESCAPE,
        ["tab"] = VK_TAB, ["space"] = VK_SPACE, ["backspace"] = VK_BACK, ["delete"] = VK_DELETE,
        ["del"] = VK_DELETE, ["insert"] = VK_INSERT, ["home"] = VK_HOME, ["end"] = VK_END,
        ["pageup"] = VK_PRIOR, ["pagedown"] = VK_NEXT,
        ["up"] = VK_UP, ["down"] = VK_DOWN, ["left"] = VK_LEFT, ["right"] = VK_RIGHT,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75,
        ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
    };

    public static bool TryNamedKey(string name, out ushort vk) => Named.TryGetValue(name.Trim(), out vk);

    /// <summary>Maps a character to its virtual key on the current layout, and whether Shift is required.</summary>
    public static bool TryChar(char c, out ushort vk, out bool shift)
    {
        var scan = VkKeyScanW(c);
        if (scan == -1) { vk = 0; shift = false; return false; }
        vk = (ushort)(scan & 0xFF);
        shift = (scan & 0x100) != 0;
        return true;
    }

    [DllImport("user32.dll")]
    private static extern short VkKeyScanW(char ch);
}
