using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Telekinesis.Abstractions;

namespace Telekinesis.Windows;

/// <summary>
/// A Windows pseudo-console session (issue #27): ConPTY hosting one child
/// process (cmd.exe by default), with a background reader pushing raw output
/// bytes to the supplied callback. Interactive programs see a real terminal —
/// colors, cursor control, Ctrl-C, TUIs.
/// </summary>
public sealed class ConPtyConsoleSession : IConsoleSession
{
    private readonly nint _pty;
    private readonly nint _process;
    private readonly FileStream _input;
    private readonly SafeFileHandle _inRead, _inWrite, _outRead, _outWrite;

    public string Shell { get; }
    public bool IsAlive => WaitForSingleObject(_process, 0) != 0;

    public ConPtyConsoleSession(string shell, int cols, int rows, Action<byte[], int> onOutput)
    {
        Shell = shell;
        if (!CreatePipe(out _inRead, out _inWrite, 0, 0) || !CreatePipe(out _outRead, out _outWrite, 0, 0))
            throw new Win32Exception();
        var hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, _inRead, _outWrite, 0, out _pty);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed.");
        // NOTE: this ConPTY build needs the parent to KEEP its copy of the output
        // write end open — closing it here EOFs the output pipe immediately (zero
        // bytes). The parent's copies are released in Dispose() instead.

        // Attribute list carrying the pseudoconsole to CreateProcess.
        var size = nint.Zero;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var attrs = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size) ||
            !UpdateProcThreadAttribute(attrs, 0, ProcThreadAttributePseudoconsole, _pty, nint.Size, 0, 0))
            throw new Win32Exception();

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        si.lpAttributeList = attrs;
        try
        {
            // ponytail: canonical ConPTY spawn (matches Microsoft's EchoCon sample:
            // EXTENDED_STARTUPINFO_PRESENT, bInheritHandles=false, no std-handle
            // redirection — the pseudoconsole attribute supplies the child's console).
            // KNOWN LIMITATION (#46): on Windows 11 ARM64 build 26200 the child does
            // not bind to the pseudoconsole and falls back to the parent console;
            // reproduced identically in an interactive (session 1) launch, so it is a
            // platform/ConPTY issue below this P/Invoke, not the session-0 trap.
            if (!CreateProcess(null, shell, 0, 0, false, ExtendedStartupinfoPresent,
                    0, null, ref si, out var pi))
                throw new Win32Exception();
            _process = pi.hProcess;
            CloseHandle(pi.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(attrs);
            Marshal.FreeHGlobal(attrs);
        }

        _input = new FileStream(_inWrite, FileAccess.Write);
        var output = new FileStream(_outRead, FileAccess.Read);
        _ = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = output.Read(buffer, 0, buffer.Length)) > 0)
                    onOutput(buffer, n);
            }
            catch (IOException) { /* pty closed */ }
            catch (ObjectDisposedException) { }
        });
    }

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _input.Write(bytes, 0, bytes.Length);
        _input.Flush();
    }

    public void Resize(int cols, int rows) =>
        ResizePseudoConsole(_pty, new COORD { X = (short)cols, Y = (short)rows });

    public void Dispose()
    {
        // Closing the pseudoconsole detaches the child's terminal; terminate the
        // child too so `console_close` never leaves an orphan shell behind.
        ClosePseudoConsole(_pty);
        if (IsAlive) TerminateProcess(_process, 0);
        CloseHandle(_process);
        _input.Dispose();     // owns _inWrite
        _inRead.Dispose(); _outWrite.Dispose(); _outRead.Dispose();
    }

    private const int ExtendedStartupinfoPresent = 0x00080000;
    private static readonly nint ProcThreadAttributePseudoconsole = 0x20016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    // Blittable layout (IntPtr, not string) so `ref STARTUPINFOEX` is a direct
    // pointer to this struct — no temporary marshalling copy that could drop
    // lpAttributeList. Correct-by-construction; note it did NOT by itself resolve
    // the #46 binding failure, which points below the P/Invoke layer.
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public nint lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, nint attrs, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out nint pty);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(nint pty, COORD size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint pty);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint prev, nint ret);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? app, string cmdLine, nint procAttrs, nint threadAttrs,
        bool inherit, int flags, nint env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint ms);
}
