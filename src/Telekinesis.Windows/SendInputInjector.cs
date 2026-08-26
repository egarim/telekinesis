using System.Runtime.InteropServices;
using Telekinesis.Abstractions;

namespace Telekinesis.Windows;

/// <summary>
/// OS-level input injection via user32 SendInput — the Windows counterpart of the
/// Linux uinput injector. Needs no special permission, but UIPI silently drops
/// input aimed at higher-integrity (elevated) windows; SendInput then reports 0
/// events injected and this class throws so the failure is loud, not silent.
/// </summary>
internal sealed class SendInputInjector
{
    /// <summary>The virtual desktop rectangle — shared with screen capture so
    /// what vision sees is exactly where clicks land.</summary>
    internal static (int X, int Y, int Width, int Height) VirtualScreen() => (
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)), Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));

    public void MoveTo(int x, int y)
    {
        // Absolute coordinates are normalized to 0..65535 over the *virtual* desktop
        // so multi-monitor and negative-origin layouts land on the right pixel.
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var nx = (int)((x - vx) * 65535.0 / (vw - 1));
        var ny = (int)((y - vy) * 65535.0 / (vh - 1));
        Send([Mouse(nx, ny, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK)]);
    }

    public void Click(PointerButton button)
    {
        var (down, up) = button switch
        {
            PointerButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            PointerButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        Send([Mouse(0, 0, down), Mouse(0, 0, up)]);
    }

    /// <summary>Types text as KEYEVENTF_UNICODE events, layout-independent. Newlines become Enter.</summary>
    public void TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                inputs.Add(Key(WindowsKeyMap.VK_RETURN, 0, 0));
                inputs.Add(Key(WindowsKeyMap.VK_RETURN, 0, KEYEVENTF_KEYUP));
                continue;
            }
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    /// <summary>Presses the keys down in order and releases them in reverse (e.g. ctrl+s).</summary>
    public void Chord(IReadOnlyList<ushort> vks)
    {
        var inputs = new INPUT[vks.Count * 2];
        for (var i = 0; i < vks.Count; i++)
            inputs[i] = Key(vks[i], 0, ExtendedFlag(vks[i]));
        for (var i = 0; i < vks.Count; i++)
        {
            var vk = vks[vks.Count - 1 - i];
            inputs[vks.Count + i] = Key(vk, 0, ExtendedFlag(vk) | KEYEVENTF_KEYUP);
        }
        Send(inputs);
    }

    /// <summary>Navigation keys share VK codes with the numpad; the extended flag picks the right one.</summary>
    private static uint ExtendedFlag(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E
        ? KEYEVENTF_EXTENDEDKEY : 0;

    private static void Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                $"SendInput injected {sent}/{inputs.Length} events (error {Marshal.GetLastWin32Error()}). "
                + "If the target window is elevated, run Telekinesis elevated too (UIPI blocks input upward).");
    }

    private static INPUT Mouse(int dx, int dy, uint flags) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } },
    };

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };

    // ---- Win32 ----

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000,
        MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010,
        MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
