using System.Runtime.InteropServices;

namespace Telekinesis.Linux;

/// <summary>
/// A virtual input device backed by /dev/uinput, used as the universal action
/// fallback when a control has no native accessibility action. Creates one
/// device exposing an absolute pointer (mapped across the desktop bounds) plus
/// mouse buttons and a keyboard, and injects pointer moves, clicks and key events.
///
/// Linux-only and requires write access to /dev/uinput (see `telekinesis setup`).
/// </summary>
internal sealed class UinputInjector : IDisposable
{
    // ---- libc ----
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buf, nint count);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl(int fd, nuint request, int arg);
    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")] private static extern int ioctl(int fd, nuint request, byte[] arg);

    private const int O_WRONLY = 0x0001, O_NONBLOCK = 0x0800;

    // Event types and codes (linux/input-event-codes.h).
    private const ushort EV_SYN = 0x00, EV_KEY = 0x01, EV_ABS = 0x03;
    private const ushort SYN_REPORT = 0, ABS_X = 0x00, ABS_Y = 0x01;
    private const int ABS_CNT = 64;

    // ioctl request codes, computed via the _IOW/_IO macros for correctness.
    private static readonly nuint UI_DEV_CREATE = Io('U', 1);
    private static readonly nuint UI_DEV_DESTROY = Io('U', 2);
    private static readonly nuint UI_SET_EVBIT = Iow('U', 100, sizeof(int));
    private static readonly nuint UI_SET_KEYBIT = Iow('U', 101, sizeof(int));
    private static readonly nuint UI_SET_ABSBIT = Iow('U', 103, sizeof(int));

    private readonly int _fd;
    private readonly int _screenW, _screenH;
    private bool _disposed;

    public UinputInjector(int screenWidth, int screenHeight)
    {
        _screenW = Math.Max(1, screenWidth);
        _screenH = Math.Max(1, screenHeight);

        _fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (_fd < 0)
            throw new IOException($"Cannot open /dev/uinput (errno {Marshal.GetLastWin32Error()}). " +
                "Run `telekinesis setup` to grant access.");

        try
        {
            Ioctl(UI_SET_EVBIT, EV_KEY);
            Ioctl(UI_SET_EVBIT, EV_ABS);
            Ioctl(UI_SET_EVBIT, EV_SYN);

            // Enable every key code we might emit, plus the three mouse buttons.
            foreach (var code in AllKeyCodes()) Ioctl(UI_SET_KEYBIT, code);
            Ioctl(UI_SET_ABSBIT, ABS_X);
            Ioctl(UI_SET_ABSBIT, ABS_Y);

            WriteUinputUserDev();
            if (ioctl(_fd, UI_DEV_CREATE, 0) < 0)
                throw new IOException($"UI_DEV_CREATE failed (errno {Marshal.GetLastWin32Error()}).");

            // The kernel needs a moment to register the device with the input stack.
            Thread.Sleep(100);
        }
        catch
        {
            close(_fd);
            throw;
        }
    }

    public void MoveTo(int screenX, int screenY)
    {
        // ABS axes span the desktop bounds, so screen pixels map 1:1.
        Emit(EV_ABS, ABS_X, Math.Clamp(screenX, 0, _screenW));
        Emit(EV_ABS, ABS_Y, Math.Clamp(screenY, 0, _screenH));
        Sync();
    }

    public void Click(int button)
    {
        Emit(EV_KEY, (ushort)button, 1); Sync();
        Emit(EV_KEY, (ushort)button, 0); Sync();
    }

    public void KeyClick(int code)
    {
        Emit(EV_KEY, (ushort)code, 1); Sync();
        Emit(EV_KEY, (ushort)code, 0); Sync();
    }

    /// <summary>Press modifiers+key together and release in reverse order.</summary>
    public void Chord(IReadOnlyList<int> codes)
    {
        for (int i = 0; i < codes.Count; i++) { Emit(EV_KEY, (ushort)codes[i], 1); Sync(); }
        for (int i = codes.Count - 1; i >= 0; i--) { Emit(EV_KEY, (ushort)codes[i], 0); Sync(); }
    }

    public void TypeText(string text)
    {
        foreach (var ch in text)
        {
            if (!LinuxKeyMap.TryChar(ch, out var code, out var shift)) continue;
            if (shift) Emit(EV_KEY, (ushort)LinuxKeyMap.KEY_LEFTSHIFT, 1);
            Emit(EV_KEY, (ushort)code, 1); Sync();
            Emit(EV_KEY, (ushort)code, 0);
            if (shift) Emit(EV_KEY, (ushort)LinuxKeyMap.KEY_LEFTSHIFT, 0);
            Sync();
        }
    }

    // ---- low-level emit ----

    private void Emit(ushort type, ushort code, int value)
    {
        // struct input_event { timeval time (16B on 64-bit); u16 type; u16 code; s32 value; }
        Span<byte> ev = stackalloc byte[24];
        BitConverter.TryWriteBytes(ev[16..], type);
        BitConverter.TryWriteBytes(ev[18..], code);
        BitConverter.TryWriteBytes(ev[20..], value);
        var buf = ev.ToArray();
        if (write(_fd, buf, buf.Length) < 0)
            throw new IOException($"uinput write failed (errno {Marshal.GetLastWin32Error()}).");
    }

    private void Sync() => Emit(EV_SYN, SYN_REPORT, 0);

    private void Ioctl(nuint request, int arg)
    {
        if (ioctl(_fd, request, arg) < 0)
            throw new IOException($"ioctl 0x{request:X} failed (errno {Marshal.GetLastWin32Error()}).");
    }

    private void WriteUinputUserDev()
    {
        // struct uinput_user_dev: char name[80]; input_id id (8B); u32 ff_effects_max;
        // s32 absmax[64]; s32 absmin[64]; s32 absfuzz[64]; s32 absflat[64].
        const int nameLen = 80;
        var buf = new byte[nameLen + 8 + 4 + ABS_CNT * 4 * 4];
        var name = "Telekinesis Virtual Input"u8;
        name.CopyTo(buf);

        int idOff = nameLen;                    // input_id: bustype, vendor, product, version (u16 each)
        BitConverter.TryWriteBytes(buf.AsSpan(idOff + 0), (ushort)0x03); // BUS_USB
        BitConverter.TryWriteBytes(buf.AsSpan(idOff + 2), (ushort)0x1234);
        BitConverter.TryWriteBytes(buf.AsSpan(idOff + 4), (ushort)0x5678);
        BitConverter.TryWriteBytes(buf.AsSpan(idOff + 6), (ushort)1);

        int absmaxOff = nameLen + 8 + 4;        // absmax[ABS_X], absmax[ABS_Y]
        BitConverter.TryWriteBytes(buf.AsSpan(absmaxOff + ABS_X * 4), _screenW);
        BitConverter.TryWriteBytes(buf.AsSpan(absmaxOff + ABS_Y * 4), _screenH);
        // absmin defaults to 0.

        if (write(_fd, buf, buf.Length) < 0)
            throw new IOException($"writing uinput_user_dev failed (errno {Marshal.GetLastWin32Error()}).");
    }

    private static IEnumerable<int> AllKeyCodes()
    {
        for (int c = 1; c <= 111; c++) yield return c;   // KEY_ESC..KEY_DELETE range
        yield return LinuxKeyMap.KEY_LEFTMETA;
        yield return LinuxKeyMap.BTN_LEFT;
        yield return LinuxKeyMap.BTN_RIGHT;
        yield return LinuxKeyMap.BTN_MIDDLE;
    }

    // _IOC(dir,type,nr,size) with NR=0, TYPE=8, SIZE=16, DIR=30 shifts; _IOC_WRITE=1.
    private static nuint Iow(char type, int nr, int size) =>
        (nuint)((1u << 30) | ((uint)type << 8) | (uint)nr | ((uint)size << 16));
    private static nuint Io(char type, int nr) =>
        (nuint)(((uint)type << 8) | (uint)nr);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_fd >= 0)
        {
            ioctl(_fd, UI_DEV_DESTROY, 0);
            close(_fd);
        }
    }
}
