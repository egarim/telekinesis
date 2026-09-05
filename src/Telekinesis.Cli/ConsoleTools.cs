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

    public Entry Open(string? shell, int cols, int rows)
    {
        cols = cols <= 0 ? 120 : cols;
        rows = rows <= 0 ? 30 : rows;
        shell = string.IsNullOrWhiteSpace(shell)
            ? OperatingSystem.IsWindows() ? "cmd.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh"
            : shell;

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
        return entry;
    }

    public Entry Get(string id) =>
        _sessions.TryGetValue(id, out var e)
            ? e
            : throw new KeyNotFoundException($"No console session '{id}' (console_list shows active ones).");

    public IReadOnlyCollection<Entry> List() => _sessions.Values.ToList();

    public void Close(string id)
    {
        if (_sessions.TryRemove(id, out var e)) e.Session.Dispose();
        else throw new KeyNotFoundException($"No console session '{id}'.");
    }

    public void Dispose()
    {
        foreach (var e in _sessions.Values) e.Session.Dispose();
        _sessions.Clear();
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
    [Description("Start a persistent interactive terminal session in a real PTY (ConPTY/openpty). Returns {sessionId, shell}. Use console_write/console_read to interact; sessions live until console_close or server exit.")]
    public static async Task<string> ConsoleOpen(
        ConsoleSessionService consoles,
        [Description("Program/command line to run; empty = the OS default shell (cmd.exe, $SHELL).")] string? shell,
        [Description("Terminal columns (default 120).")] int cols,
        [Description("Terminal rows (default 30).")] int rows,
        CancellationToken ct)
    {
        var entry = consoles.Open(shell, cols, rows);
        AuditLog.Append("console_open", entry.Session.Shell, true, entry.Id);
        await Task.Delay(300, ct); // let the shell paint its banner/prompt
        return JsonSerializer.Serialize(new
        {
            sessionId = entry.Id,
            shell = entry.Session.Shell,
            screen = entry.Screen.Render(),
        }, PerceptionTools.Json);
    }

    [McpServerTool(Name = "console_write")]
    [Description("Write text to a console session's stdin. sendEnter appends the Enter key. Send \"\\u0003\" for Ctrl-C.")]
    public static async Task<string> ConsoleWrite(
        ConsoleSessionService consoles,
        [Description("Session id from console_open.")] string sessionId,
        [Description("The text to type.")] string text,
        [Description("Press Enter after the text (default true).")] bool sendEnter,
        CancellationToken ct)
    {
        var entry = consoles.Get(sessionId);
        entry.Session.Write(sendEnter ? text + "\r" : text);
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
    [Description("Resize the PTY (TUI apps re-layout).")]
    public static Task<string> ConsoleResize(
        ConsoleSessionService consoles, string sessionId,
        [Description("New column count.")] int cols,
        [Description("New row count.")] int rows,
        CancellationToken ct)
    {
        var entry = consoles.Get(sessionId);
        entry.Session.Resize(cols, rows);
        entry.Screen.Resize(cols, rows);
        AuditLog.Append("console_resize", $"{sessionId}: {cols}x{rows}", true, "pty");
        return Task.FromResult(JsonSerializer.Serialize(new { ok = true }, PerceptionTools.Json));
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
