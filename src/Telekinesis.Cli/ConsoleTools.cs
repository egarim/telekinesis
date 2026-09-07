using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Owns the live PTY sessions (issue #27): one <see cref="IConsoleSession"/> +
/// <see cref="TerminalScreen"/> per console_open. DI singleton; sessions die
/// with the server process.
/// </summary>
public sealed class ConsoleSessionService : IDisposable
{
    public sealed record Entry(string Id, IConsoleSession Session, TerminalScreen Screen, DateTimeOffset Opened);

    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private int _next;
    private volatile bool _disposed;

    public Entry Open(string? shell, int cols, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 0/omitted means "use the default"; anything else is clamped to a grid we
        // can actually allocate (issue #58). Clamp ONCE here so the PTY and the
        // TerminalScreen are created with identical dimensions — the screen clamps
        // internally, and a larger PTY would wrap output the screen cannot hold.
        cols = cols <= 0 ? 120 : TerminalScreen.ClampDimension(cols);
        rows = rows <= 0 ? 30 : TerminalScreen.ClampDimension(rows);
        shell = string.IsNullOrWhiteSpace(shell)
            ? OperatingSystem.IsWindows() ? "cmd.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh"
            : shell;

        // Windows ConPTY is not enabled by default: on Win11 ARM64 (build 26200) the
        // child process does not bind to the pseudoconsole and renders to the parent
        // console instead — which would corrupt the MCP stdio stream (issue #46, its
        // "Windows ConPTY child-attach" follow-up). The implementation ships and is
        // canonical; opt in with TELEKINESIS_CONPTY=1 to exercise it once a fixing
        // Windows build lands. Linux/macOS PTY sessions are fully supported.
        if (OperatingSystem.IsWindows() &&
            Environment.GetEnvironmentVariable("TELEKINESIS_CONPTY") != "1")
            throw new PlatformNotSupportedException(
                "Interactive console sessions are not yet supported on Windows "
                + "(ConPTY child-attach limitation, issue #46). Linux/macOS are supported; "
                + "set TELEKINESIS_CONPTY=1 to force-enable the experimental Windows path.");

        var screen = new TerminalScreen(cols, rows);
        IConsoleSession session =
#if WINDOWS
            new Telekinesis.Windows.ConPtyConsoleSession(shell, cols, rows, screen.Feed);
#else
            OperatingSystem.IsWindows()
                ? throw new PlatformNotSupportedException(
                    "This build does not include ConPTY; run the net10.0-windows target.")
                : new Telekinesis.Linux.UnixPtyConsoleSession(shell, cols, rows, screen.Feed);
#endif
        var entry = new Entry($"con{Interlocked.Increment(ref _next)}", session, screen, DateTimeOffset.Now);
        _sessions[entry.Id] = entry;
        // If Dispose() raced ahead of the insert, this session would leak — reap it now.
        if (_disposed && _sessions.TryRemove(entry.Id, out _))
        {
            session.Dispose();
            throw new ObjectDisposedException(nameof(ConsoleSessionService));
        }
        return entry;
    }

    public Entry Get(string id) =>
        _sessions.TryGetValue(id, out var e)
            ? e
            : throw new KeyNotFoundException($"No console session '{id}' (console_list shows active ones).");

    public IReadOnlyCollection<Entry> List() => _sessions.Values.ToList();

    public void Close(string id)
    {
        // TryRemove makes exactly one caller win the entry, so Close/Dispose never
        // double-dispose the same session.
        if (_sessions.TryRemove(id, out var e)) e.Session.Dispose();
        else throw new KeyNotFoundException($"No console session '{id}'.");
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var id in _sessions.Keys)
            if (_sessions.TryRemove(id, out var e))
                e.Session.Dispose();
    }
}

/// <summary>
/// Interactive console MCP tools (issue #27) — action set: loaded only with
/// actions enabled, every mutating call audit-logged. The read-back is the
/// rendered screen, exactly what a human at the terminal would see.
/// </summary>
[McpServerToolType]
public static class ConsoleTools
{
    [McpServerTool(Name = "console_open")]
    [Description("Start a persistent interactive terminal session in a real PTY (openpty on Linux/macOS; Windows ConPTY is not yet supported — issue #46). Returns {sessionId, shell}. Use console_write/console_read to interact; sessions live until console_close or server exit.")]
    public static async Task<string> ConsoleOpen(
        ConsoleSessionService consoles,
        [Description("Program/command line to run; empty = the OS default shell (cmd.exe, $SHELL).")] string? shell,
        [Description("Terminal columns (default 120, max 1000).")] int cols,
        [Description("Terminal rows (default 30, max 1000).")] int rows,
        CancellationToken ct)
    {
        var entry = consoles.Open(shell, cols, rows);
        AuditLog.Append("console_open", entry.Session.Shell, true, entry.Id);
        await Task.Delay(300, ct); // let the shell paint its banner/prompt
        return JsonSerializer.Serialize(new
        {
            sessionId = entry.Id,
            shell = entry.Session.Shell,
            cols = entry.Screen.Cols,
            rows = entry.Screen.Rows,
            screen = entry.Screen.Render(),
        }, PerceptionTools.Json);
    }

    [McpServerTool(Name = "console_write")]
    [Description("Write text to a console session's stdin. sendEnter appends the Enter key. Send \"\\u0003\" for Ctrl-C.")]
    public static async Task<string> ConsoleWrite(
        ConsoleSessionService consoles,
        [Description("Session id from console_open.")] string sessionId,
        [Description("The text to type.")] string text,
        // Nullable so an OMITTED value is distinguishable from an explicit false and
        // can default to true (issue #57). A plain `bool` deserialized to false when
        // omitted, so the command was typed but never submitted — which reads as a
        // hung session. Same idiom as ActionTools' `string? button`.
        [Description("Press Enter after the text (default true). Pass false to type without submitting.")] bool? sendEnter,
        CancellationToken ct)
    {
        var entry = consoles.Get(sessionId);
        entry.Session.Write(sendEnter is not false ? text + "\r" : text);
        AuditLog.Append("console_write", $"{sessionId}: {text}", true, "pty");
        await Task.Delay(250, ct); // give the program a beat to react before the usual read
        return JsonSerializer.Serialize(new { ok = true, alive = entry.Session.IsAlive }, PerceptionTools.Json);
    }

    [McpServerTool(Name = "console_read")]
    [Description("Read the session's current visible screen as plain text (ANSI rendered away). Poll after console_write; the screen is a snapshot, not a stream.")]
    public static Task<string> ConsoleRead(
        ConsoleSessionService consoles,
        [Description("Session id from console_open.")] string sessionId,
        [Description("Only the last N lines (0 = whole screen).")] int lines,
        CancellationToken ct)
    {
        var entry = consoles.Get(sessionId);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            screen = entry.Screen.Render(lines > 0 ? lines : null),
            alive = entry.Session.IsAlive,
        }, PerceptionTools.Json));
    }

    [McpServerTool(Name = "console_resize")]
    [Description("Resize the PTY (TUI apps re-layout). Dimensions are clamped to 2-1000; the reply reports the size actually applied.")]
    public static Task<string> ConsoleResize(
        ConsoleSessionService consoles, string sessionId,
        [Description("New column count.")] int cols,
        [Description("New row count.")] int rows,
        CancellationToken ct)
    {
        var entry = consoles.Get(sessionId);
        // Clamp here too, so the PTY and the screen are told the SAME size — the
        // screen clamps internally, and a divergent PTY size would mis-wrap output.
        cols = TerminalScreen.ClampDimension(cols);
        rows = TerminalScreen.ClampDimension(rows);
        entry.Session.Resize(cols, rows);
        entry.Screen.Resize(cols, rows);
        AuditLog.Append("console_resize", $"{sessionId}: {cols}x{rows}", true, "pty");
        // Report the applied size: a caller that asked for something out of range
        // needs to know what it actually got.
        return Task.FromResult(JsonSerializer.Serialize(new { ok = true, cols, rows }, PerceptionTools.Json));
    }

    [McpServerTool(Name = "console_close")]
    [Description("Terminate a console session and its child process.")]
    public static Task<string> ConsoleClose(ConsoleSessionService consoles, string sessionId, CancellationToken ct)
    {
        consoles.Close(sessionId);
        AuditLog.Append("console_close", sessionId, true, "pty");
        return Task.FromResult(JsonSerializer.Serialize(new { ok = true }, PerceptionTools.Json));
    }

    [McpServerTool(Name = "console_list")]
    [Description("List active console sessions.")]
    public static Task<string> ConsoleList(ConsoleSessionService consoles, CancellationToken ct) =>
        Task.FromResult(JsonSerializer.Serialize(
            consoles.List().Select(e => new
            {
                sessionId = e.Id,
                shell = e.Session.Shell,
                alive = e.Session.IsAlive,
                opened = e.Opened,
            }), PerceptionTools.Json));
}
