using System.Runtime.InteropServices;

namespace Telekinesis.Cli;

/// <summary>
/// Detects the "wrong session" trap on Windows: a process started over SSH or by a
/// service lives outside the interactive console session, where UIA sees no windows
/// and SendInput reaches no desktop. See docs/HEADLESS-CLI.md.
/// </summary>
internal static class WindowsSession
{
    public static bool NeedsRelay()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (Environment.GetEnvironmentVariable("TELEKINESIS_NO_RELAY") == "1") return false;
        var console = WTSGetActiveConsoleSessionId();
        return console != 0xFFFFFFFF
            && (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId != console;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
