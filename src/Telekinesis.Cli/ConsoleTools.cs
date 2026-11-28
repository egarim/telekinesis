using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;
using Telekinesis.Sentiora;

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

    /// <summary>Test seam: replaces the backend factory. Must be set before the first
    /// Open(), because the Lazy below captures whichever factory is current on its
    /// first access.</summary>
    private volatile Func<SentioraBackend>? _sentioraFactory;

    /// <summary>The Sentiora backend, created on first use and only when
    /// TELEKINESIS_SENTIORA=1, so a Telekinesis that never opts in never spawns a Node
    /// process (issue #67).
    ///
    /// Lazy with ExecutionAndPublication rather than `_sentiora ??= new()`: every other
    /// part of Open() is built for concurrent callers — _sessions is a
    /// ConcurrentDictionary, ids come from Interlocked.Increment, and Register() guards
    /// a Dispose() race. An unsynchronised read-modify-write here would let two
    /// concurrent console_open calls each construct a backend; one assignment then
    /// overwrites the other, Dispose() sees only the survivor, and if the discarded
    /// instance had already reached OpenAsync and started a host, that Node process is
    /// orphaned with nothing left holding a reference to it.</summary>
    private readonly Lazy<SentioraBackend> _sentiora;

    public ConsoleSessionService()
    {
        _sentiora = new Lazy<SentioraBackend>(() =>
        {
            // Read the seam once: it is volatile, and reading it twice could see the
            // test factory on one read and null on the other.
            var factory = _sentioraFactory;
            return factory is not null ? factory() : new SentioraBackend();
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

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

        // Opt-in Sentiora Terminal backend (issue #67), tried BEFORE the Windows gate
        // below — it is the path that can serve Windows while ConPTY child-attach is
        // broken (#46/#49). Off unless TELEKINESIS_SENTIORA=1, and when it fails the
        // reason is announced on stderr and we fall through to the native backend:
        // #67 requires it to be unambiguous which backend is active, so a silent
        // fallback is the one thing this must not do.
        if (SentioraBackend.IsEnabled)
        {
            var sentioraScreen = new TerminalScreen(cols, rows);
            try
            {
                var sentioraSession = _sentiora.Value.Open(shell, cols, rows, sentioraScreen.Feed);
                // The runtime clamps harder than Telekinesis does (cols 2-300, rows
                // 2-150 vs 2-1000), so the grid must be told the size the PTY actually
                // got — otherwise console_open reports dimensions the child never had.
                if (sentioraSession is IConsoleSessionGeometry geometry &&
                    (geometry.AppliedCols != cols || geometry.AppliedRows != rows))
                    sentioraScreen.Resize(geometry.AppliedCols, geometry.AppliedRows);
                return Register(sentioraSession, sentioraScreen);
            }
            // A disposed service must still surface as disposed rather than being
            // mistaken for a backend failure and retried on the native path.
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                Console.Error.WriteLine(
                    "[telekinesis] TELEKINESIS_SENTIORA=1 but the Sentiora console backend could not be "
                    + $"used ({ex.Message}); falling back to the native console backend.");
            }
        }

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
                + "set TELEKINESIS_CONPTY=1 to force-enable the experimental Windows path, "
                + "or TELEKINESIS_SENTIORA=1 for the out-of-process Sentiora backend (issue #67).");

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
        return Register(session, screen);
    }

    private Entry Register(IConsoleSession session, TerminalScreen screen)
    {
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

    /// <summary>Seam for tests: register a fake session so the tool logic can be
    /// exercised without spawning a real PTY (same pattern as CdpSession's
    /// RegisterForTest).</summary>
    internal Entry RegisterForTest(IConsoleSession session, TerminalScreen screen)
    {
        var entry = new Entry($"con{Interlocked.Increment(ref _next)}", session, screen, DateTimeOffset.Now);
        _sessions[entry.Id] = entry;
        return entry;
    }

    /// <summary>Seam for tests: install a Sentiora backend instead of letting Open()
    /// build one from discovery (issue #67). Must be called before the first Open().
    /// TELEKINESIS_SENTIORA=1 must still be set, so the gate itself is exercised too.</summary>
    internal void UseSentioraBackendForTest(SentioraBackend backend) => _sentioraFactory = () => backend;

    /// <summary>Seam for tests: replace how the backend is constructed, so a test can
    /// count constructions and prove the lazy init is atomic. Must be called before the
    /// first Open().</summary>
    internal void UseSentioraFactoryForTest(Func<SentioraBackend> factory) => _sentioraFactory = factory;

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
        // After the sessions, so no session tries to reach a host that is already gone.
        // IsValueCreated first: disposing must not CONSTRUCT a backend that was never
        // used, which is what an unconditional _sentiora.Value would do.
        if (_sentiora.IsValueCreated) _sentiora.Value.Dispose();
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
    [Description("Start a persistent interactive terminal session in a real PTY (openpty on Linux/macOS; native Windows ConPTY is not yet supported — issue #46 — but TELEKINESIS_SENTIORA=1 selects an out-of-process backend that works there, issue #67). Returns {sessionId, shell}. Use console_write/console_read to interact; sessions live until console_close or server exit.")]
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
        // The `= true` is what actually makes this optional (issue #57). The SDK puts
        // a parameter in the schema's `required` list unless it has a C# DEFAULT, and
        // a missing required argument throws rather than binding null — so nullability
        // alone did not fix the bug. Nullable on top of the default so an explicit
        // null also means "yes" instead of failing to bind. Pinned by the schema test.
        [Description("Press Enter after the text (default true). Pass false to type without submitting.")] bool? sendEnter = true,
        CancellationToken ct = default)
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
        // A backend may not be able to honour the request: the Sentiora runtime clamps
        // to cols 2-300 / rows 2-150 while Telekinesis clamps to 2-1000, so a 500-column
        // resize is unachievable there. Report and render the size ACTUALLY applied —
        // which is what this tool has always promised — and keep the grid in step with
        // the PTY so output does not mis-wrap. Backends without this interface honour
        // the request exactly and are unaffected.
        if (entry.Session is IConsoleSessionGeometry geometry)
        {
            cols = geometry.AppliedCols;
            rows = geometry.AppliedRows;
        }
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
