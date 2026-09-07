using System.Text.Json;

namespace Telekinesis.Cli;

/// <summary>
/// File audit trail for every action tool call, in addition to the stderr line.
/// One JSON object per line at $XDG_STATE_HOME/telekinesis/audit.log (fallback:
/// ~/.local/state/telekinesis/audit.log; on Windows %LOCALAPPDATA%\Telekinesis\state).
/// fill_credential logs only field metadata, never the secret. NOTE console_write
/// logs its text verbatim (a shell command is the audit trail), so this file is
/// only as clean as what callers type into a PTY — see docs/CONSOLE.md#security.
/// </summary>
internal static class AuditLog
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(StateDir(), "audit.log");

    private static string StateDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var root = !string.IsNullOrEmpty(xdg)
            ? xdg
            : OperatingSystem.IsWindows()
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telekinesis", "state")
                : System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return System.IO.Path.Combine(root, "telekinesis");
    }

    public static void Append(string tool, string target, bool success, string path)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.Now,
                tool,
                target,
                success,
                path,
            });
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line + Environment.NewLine);
            }
        }
        catch
        {
            // The audit file must never take an action down with it; stderr still has the line.
        }
    }
}
