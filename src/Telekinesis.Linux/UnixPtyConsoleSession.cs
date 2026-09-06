using System.Runtime.InteropServices;
using System.Text;
using Telekinesis.Abstractions;

namespace Telekinesis.Linux;

/// <summary>
/// A Unix pseudo-terminal session (issue #27) for Linux and macOS. Uses
/// <c>openpty</c> (master/slave fds, no fork) + <c>posix_spawn</c> so the fork
/// and exec happen entirely inside libc — async-signal-safe, with NO managed
/// code running in the forked child (the fork-then-managed-code pattern can
/// deadlock in a multi-threaded runtime; issue #46 review). A background reader
/// pushes raw output bytes to the supplied callback.
/// </summary>
public sealed partial class UnixPtyConsoleSession : IConsoleSession
{
    private readonly int _master;
    private readonly int _pid;
    private volatile bool _exited;
    private int _disposed;

    public string Shell { get; }
    public bool IsAlive => !_exited && Kill(_pid, 0) == 0;

    public UnixPtyConsoleSession(string shell, int cols, int rows, Action<byte[], int> onOutput)
    {
        Shell = shell;
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
        if (OpenPty(out _master, out var slave, nint.Zero, nint.Zero, ref size) != 0)
            throw new InvalidOperationException("openpty failed.");

        var fa = Marshal.AllocHGlobal(1024); // opaque; sized generously for both OSes
        var attr = Marshal.AllocHGlobal(1024);
        try
        {
            FileActionsInit(fa);
            // The child's stdin/out/err become the slave; then drop its extra fds.
            FileActionsAddDup2(fa, slave, 0);
            FileActionsAddDup2(fa, slave, 1);
            FileActionsAddDup2(fa, slave, 2);
            FileActionsAddClose(fa, slave);
            FileActionsAddClose(fa, _master);

            AttrInit(attr);
            // SETSID: the child leads a new session, and opening the slave as fd 0
            // makes it the controlling terminal — so job control / Ctrl-C work.
            AttrSetFlags(attr, PosixSpawnSetsid);

            var argv = new[] { "/bin/sh", "-c", shell, null };
            var envp = BuildEnvp();
            if (PosixSpawn(out _pid, "/bin/sh", fa, attr, argv, envp) != 0)
                throw new InvalidOperationException("posix_spawn failed.");
        }
        finally
        {
            AttrDestroy(attr);
            FileActionsDestroy(fa);
            Marshal.FreeHGlobal(attr);
            Marshal.FreeHGlobal(fa);
            Close(slave); // parent keeps only the master end
        }

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
            WaitPid(_pid, out _, WNOHANG); // reap a self-exited child (e.g. `exit`) — no zombie
        });
    }

    private static string?[] BuildEnvp()
    {
        var env = Environment.GetEnvironmentVariables();
        var list = new List<string?>(env.Count + 1);
        foreach (System.Collections.DictionaryEntry e in env)
            list.Add($"{e.Key}={e.Value}");
        list.Add(null); // char** must be null-terminated
        return [.. list];
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        // write(2) on a PTY master can be partial — loop or a large paste loses its tail.
        var off = 0;
        while (off < bytes.Length)
        {
            var n = (int)WriteFd(_master, in bytes[off], bytes.Length - off);
            if (n <= 0) break; // fd closed / error
            off += n;
        }
    }

    public void Resize(int cols, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
        _ = Ioctl(_master, TIOCSWINSZ, ref size);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
        if (IsAlive) Kill(_pid, 9);
        _ = WaitPid(_pid, out _, 0); // reap — no zombies
        _ = Close(_master);
        _exited = true;
    }

    // TIOCSWINSZ differs per OS (0x5414 Linux, 0x80087467 BSD/macOS).
    private static readonly nuint TIOCSWINSZ = OperatingSystem.IsMacOS() ? 0x80087467 : 0x5414;

    private const int WNOHANG = 1;

    // POSIX_SPAWN_SETSID: glibc 0x80, macOS/xnu 0x0400.
    private static readonly short PosixSpawnSetsid = (short)(OperatingSystem.IsMacOS() ? 0x0400 : 0x0080);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinSize { public ushort Rows, Cols, XPixels, YPixels; }

    // openpty/forkpty live in libutil on Linux (libutil.so.1); on macOS they are in
    // libSystem, reachable as "libc". The resolver maps "util" → libutil.so.1 on Linux.
    static UnixPtyConsoleSession()
    {
        NativeLibrary.SetDllImportResolver(typeof(UnixPtyConsoleSession).Assembly, (name, asm, path) =>
            name == "util" && OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libutil.so.1", out var h)
                ? h : nint.Zero);
    }

    [DllImport("util", EntryPoint = "openpty", SetLastError = true)]
    private static extern int OpenPty(out int master, out int slave, nint name, nint termp, ref WinSize win);

    // posix_spawn family — all in libc/libSystem on both platforms.
    [DllImport("libc", EntryPoint = "posix_spawn", SetLastError = true)]
    private static extern int PosixSpawn(out int pid, string path, nint fileActions, nint attr,
        [In] string?[] argv, [In] string?[] envp);

    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    private static extern int FileActionsInit(nint fa);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    private static extern int FileActionsDestroy(nint fa);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    private static extern int FileActionsAddDup2(nint fa, int fd, int newfd);
    [DllImport("libc", EntryPoint = "posix_spawn_file_actions_addclose")]
    private static extern int FileActionsAddClose(nint fa, int fd);
    [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
    private static extern int AttrInit(nint attr);
    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    private static extern int AttrDestroy(nint attr);
    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    private static extern int AttrSetFlags(nint attr, short flags);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fd, byte[] buffer, nint count);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint WriteFd(int fd, in byte buffer, nint count);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fd, nuint request, ref WinSize size);
    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);
    [DllImport("libc", EntryPoint = "waitpid")]
    private static extern int WaitPid(int pid, out int status, int options);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
