using System.Runtime.InteropServices;
using System.Text;
using Telekinesis.Abstractions;

namespace Telekinesis.Linux;

/// <summary>
/// A Unix pseudo-terminal session (issue #27) for Linux and macOS: forkpty +
/// execvp, the same pattern the established .NET PTY hosts use (fork from
/// managed code is formally unsupported, but the child calls execvp immediately
/// and never re-enters the runtime — the practical, widely shipped approach).
/// A background reader pushes raw output bytes to the supplied callback.
/// </summary>
public sealed partial class UnixPtyConsoleSession : IConsoleSession
{
    private readonly int _master;
    private readonly int _pid;
    private volatile bool _exited;

    public string Shell { get; }
    public bool IsAlive => !_exited && Kill(_pid, 0) == 0;

    public UnixPtyConsoleSession(string shell, int cols, int rows, Action<byte[], int> onOutput)
    {
        Shell = shell;
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
        var pid = ForkPty(out var master, nint.Zero, nint.Zero, ref size);
        if (pid < 0) throw new InvalidOperationException("forkpty failed.");
        if (pid == 0)
        {
            // CHILD: exec immediately — nothing but exec may run here.
            Execvp("/bin/sh", ["/bin/sh", "-c", shell, null!]);
            Exit(127); // exec failed
        }
        _pid = pid;
        _master = master;

        _ = Task.Run(() =>
        {
            var buffer = new byte[4096];
            while (true)
            {
                var n = Read(_master, buffer, buffer.Length);
                if (n <= 0) break; // EOF/EIO: child gone
                onOutput(buffer, (int)n);
            }
            _exited = true;
        });
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _ = WriteFd(_master, bytes, bytes.Length);
    }

    public void Resize(int cols, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
        _ = Ioctl(_master, TIOCSWINSZ, ref size);
    }

    public void Dispose()
    {
        if (IsAlive) Kill(_pid, 9);
        _ = WaitPid(_pid, out _, 0); // reap — no zombies
        _ = Close(_master);
        _exited = true;
    }

    // TIOCSWINSZ differs per OS (0x5414 Linux, 0x80087467 BSD/macOS).
    private static readonly nuint TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize { public ushort Rows, Cols, XPixels, YPixels; }

    // libutil ships forkpty on both Linux (libutil.so.1) and macOS (libutil.dylib →
    // libSystem). The resolver covers distros where plain "util" doesn't probe.
    static UnixPtyConsoleSession()
    {
        NativeLibrary.SetDllImportResolver(typeof(UnixPtyConsoleSession).Assembly, (name, asm, path) =>
            name == "util" && OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libutil.so.1", out var h)
                ? h : nint.Zero);
    }

    [DllImport("util", EntryPoint = "forkpty", SetLastError = true)]
    private static extern int ForkPty(out int master, nint name, nint termios, ref WinSize size);

    [DllImport("libc", EntryPoint = "execvp", SetLastError = true)]
    private static extern int Execvp(string file, string?[] argv);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void Exit(int code);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fd, byte[] buffer, nint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteFd(int fd, byte[] buffer, nint count);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, nuint request, ref WinSize size);

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    [DllImport("libc", EntryPoint = "waitpid")]
    private static extern int WaitPid(int pid, out int status, int options);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
