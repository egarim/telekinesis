using System.Text;
using System.Text.Json.Nodes;
using Telekinesis.Abstractions;

namespace Telekinesis.Sentiora;

/// <summary>
/// An <see cref="IConsoleSession"/> backed by one Sentiora Terminal session
/// (issue #67): a child PTY hosted by an out-of-process Node runtime and reached over
/// newline-delimited JSON-RPC.
///
/// <para><b>THE RAW-BYTE STREAM IS NOT FAITHFUL, AND CANNOT BE.</b>
/// <see cref="IConsoleSession"/>'s contract is that implementations push raw output
/// bytes to the callback supplied at construction. Sentiora has no push channel and
/// never exposes raw bytes — it exposes the <i>rendered screen</i> after its terminal
/// emulator has applied the escape sequences. This adapter therefore synthesises an
/// approximation: a background <c>terminal.wait</c> loop polls for a new revision and
/// repaints the screen by pushing <c>ESC[2J ESC[H</c> followed by the whole rendered
/// grid. The visible result converges on what Sentiora shows, but the stream itself is
/// lossy in ways no adapter can undo:</para>
/// <list type="bullet">
/// <item>content that scrolled off Sentiora's viewport is gone — an observation
/// carries only the visible rows, never scrollback;</item>
/// <item>byte-level framing is gone — the child's writes are coalesced into whatever
/// the poll interval happened to catch;</item>
/// <item>escape sequences with no visible effect (SGR colour, OSC titles, cursor
/// save/restore) are gone — Sentiora's screen is already colour-stripped;</item>
/// <item>granularity is the poll timeout (~250 ms), not the child's write
/// granularity.</item>
/// </list>
/// <para>Consume this backend through <c>console_read</c>, which is already documented
/// as "a snapshot, not a stream". Do not treat the callback as a byte-level record of
/// what the child printed.</para>
/// </summary>
public sealed class SentioraConsoleSession : IConsoleSession, IConsoleSessionGeometry
{
    /// <summary>How long each background wait blocks. There is no cancellation
    /// primitive in the protocol, so this value IS the cancellation granularity.</summary>
    internal const int PollTimeoutMs = 250;

    /// <summary>Sentiora caps terminal.type at 65536 characters; longer input is chunked.</summary>
    internal const int MaxInputChars = 65536;

    /// <summary>Retries for "Stale observation". Two of the first server-level test runs
    /// on the Sentiora side hit this before a retry loop existed, so it is expected
    /// under any concurrent output, not a corner case.</summary>
    internal const int MaxWriteRetries = 5;

    private readonly SentioraHost _host;
    private readonly Action<byte[], int> _onOutput;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _pollCancellation = new();
    private readonly object _gate = new();
    private readonly Task _pollLoop;

    private string _state = "spawned";
    private long _revision;
    private string _lastPainted = string.Empty;
    private int _appliedCols, _appliedRows;
    private int _disposed;
    private volatile bool _backendLost;

    /// <summary>Sentiora's session id (a UUID). Telekinesis's own id — con1, con2, … —
    /// is assigned by ConsoleSessionService and never leaves it, so the two namespaces
    /// do not collide and nothing here has to translate between them.</summary>
    internal string RuntimeSessionId { get; }

    /// <summary>Bearer credential: anyone holding it can type into this session. It is
    /// sent on every mutating call and is never logged, never placed in an exception
    /// message, and never returned to a caller.</summary>
    private string Owner { get; }

    /// <summary>Sentiora does not echo the command back, so the command line we asked
    /// for is remembered here — it is what console_open and console_list report.</summary>
    public string Shell { get; }

    /// <summary>Lags reality by design: exit detection on the runtime side measured at
    /// ~1025 ms (node-pty's flush interval), so a child that just died can still read
    /// as alive for about a second. <c>state</c> is used rather than <c>running</c>
    /// because it distinguishes "exited" from "released".</summary>
    public bool IsAlive
    {
        get { lock (_gate) return _state is "spawned" or "operational"; }
    }

    public int AppliedCols { get { lock (_gate) return _appliedCols; } }
    public int AppliedRows { get { lock (_gate) return _appliedRows; } }

    /// <summary>Set when the runtime stopped knowing about this session (host restart
    /// or crash), so a later write says "backend lost" instead of retrying blindly.</summary>
    public bool BackendLost => _backendLost;

    private SentioraConsoleSession(
        SentioraHost host, string shell, string runtimeSessionId, string owner,
        SentioraObservation initial, Action<byte[], int> onOutput)
    {
        _host = host;
        Shell = shell;
        RuntimeSessionId = runtimeSessionId;
        Owner = owner;
        _onOutput = onOutput;
        _revision = initial.Revision;
        _state = initial.State;
        _appliedCols = initial.Cols;
        _appliedRows = initial.Rows;
        // Paint the create-time screen synchronously: console_open waits 300 ms and
        // then renders, and that wait should not be what makes the banner appear.
        Paint(initial.Screen);
        _pollLoop = Task.Run(PollAsync);
    }

    internal static async Task<SentioraConsoleSession> CreateAsync(
        SentioraHost host, string shell, int requestedCols, int requestedRows,
        Action<byte[], int> onOutput, CancellationToken ct = default)
    {
        var (command, args) = SentioraCommandLine.Split(shell);
        // Clamp to the runtime's range BEFORE the call. Telekinesis allows 2-1000 and
        // Sentiora allows cols 2-300 / rows 2-150; sending 500 would come back as
        // "Invalid dimensions" instead of being clamped.
        var cols = SentioraDimensions.ClampCols(requestedCols);
        var rows = SentioraDimensions.ClampRows(requestedRows);

        var parameters = new JsonObject { ["command"] = command, ["cols"] = cols, ["rows"] = rows };
        var argv = new JsonArray();
        foreach (var arg in args) argv.Add(arg);
        parameters["args"] = argv;

        if (!host.TryReserveSession())
            throw new SentioraProtocolException(
                "The Sentiora console backend has no room for another session on this host process.",
                "Session limit") { ErrorCode = -32000 };

        JsonNode? result;
        try
        {
            result = await host.RequestAsync("terminal.create", parameters, ct).ConfigureAwait(false);
        }
        catch
        {
            host.UnreserveSession();
            throw;
        }

        var initial = SentioraObservation.From(result);
        if (result is not JsonObject o ||
            o["owner"] is not JsonValue ownerValue || !ownerValue.TryGetValue<string>(out var owner))
        {
            host.UnreserveSession();
            throw new SentioraTransportException("The Sentiora runtime returned no owner token for the new session.");
        }

        return new SentioraConsoleSession(host, shell, initial.SessionId, owner, initial, onOutput);
    }

    public void Write(string text) => WriteAsync(text).GetAwaiter().GetResult();

    internal async Task WriteAsync(string text, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (BackendLost)
            throw new SentioraProtocolException(
                "The Sentiora console backend lost this session; open a new one.", "Unknown session");

        // Chunked because the runtime rejects anything over 65536 characters. Note
        // that chunk boundaries are NOT child-visible input boundaries: ConPTY
        // coalesces and splits writes on its own schedule, so a caller must compare
        // the ordered byte stream, not count writes.
        if (text.Length == 0)
        {
            await WriteChunkAsync(string.Empty, ct).ConfigureAwait(false);
            return;
        }
        for (var offset = 0; offset < text.Length; offset += MaxInputChars)
            await WriteChunkAsync(text.Substring(offset, Math.Min(MaxInputChars, text.Length - offset)), ct)
                .ConfigureAwait(false);
    }

    private async Task WriteChunkAsync(string text, CancellationToken ct)
    {
        // Serialised per session: the runtime will not arbitrate between two callers
        // racing on the same revision, and the adapter is the single owner, so any
        // concurrency at the Telekinesis level has to be resolved here.
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var revision = Revision;
                try
                {
                    var result = await _host.RequestAsync("terminal.type", new JsonObject
                    {
                        ["sessionId"] = RuntimeSessionId,
                        ["owner"] = Owner,
                        ["revision"] = revision,
                        ["text"] = text,
                    }, ct).ConfigureAwait(false);

                    // terminal.type bumps the revision ITSELF and returns the new one.
                    // The poll loop must be armed with THAT revision, not the one read
                    // before the write — waiting on the pre-write revision returns
                    // immediately, before the child has replied.
                    if (result is JsonObject o && o["revision"] is JsonValue v && v.TryGetValue<long>(out var next))
                        SetRevision(next);
                    return;
                }
                catch (SentioraProtocolException ex)
                    when (ex.Kind == SentioraErrorKind.StaleObservation && attempt < MaxWriteRetries)
                {
                    // The child painted between our observation and our write, so the
                    // revision we presented is already history. Re-observe and retry.
                    await ObserveAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Resize(int cols, int rows)
    {
        try
        {
            ResizeAsync(cols, rows).GetAwaiter().GetResult();
        }
        catch (SentioraTransportException)
        {
            // The host is gone. The native backends ignore a failed resize on a dead
            // PTY rather than throwing at the caller; match that, and let the next
            // write surface the loss where it can be acted on.
            _backendLost = true;
        }
    }

    internal async Task ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cols = SentioraDimensions.ClampCols(cols);
        rows = SentioraDimensions.ClampRows(rows);

        // Record the clamped size even if the call fails: console_resize reports the
        // dimensions actually applied, and what this backend would apply is the
        // clamped pair, not the pair Telekinesis asked for.
        lock (_gate) { _appliedCols = cols; _appliedRows = rows; }

        if (BackendLost || !IsAlive) return;
        try
        {
            var result = await _host.RequestAsync("terminal.resize", new JsonObject
            {
                ["sessionId"] = RuntimeSessionId,
                ["owner"] = Owner,
                ["cols"] = cols,
                ["rows"] = rows,
            }, ct).ConfigureAwait(false);
            Apply(SentioraObservation.From(result));
        }
        catch (SentioraProtocolException ex)
            when (ex.Kind is SentioraErrorKind.ResizeDenied or SentioraErrorKind.SessionNotRunning)
        {
            // The child exited or the session closed between the liveness check and
            // the call. Nothing to resize; the state will be picked up by the poll loop.
        }
    }

    /// <summary>Background wait loop. Polls rather than blocking indefinitely because
    /// the protocol has no cancellation primitive: an in-flight terminal.wait cannot
    /// be aborted, it completes or times out, so the timeout IS the shutdown latency.</summary>
    private async Task PollAsync()
    {
        var ct = _pollCancellation.Token;
        while (!ct.IsCancellationRequested)
        {
            SentioraObservation observation;
            try
            {
                var result = await _host.RequestAsync("terminal.wait", new JsonObject
                {
                    ["sessionId"] = RuntimeSessionId,
                    ["afterRevision"] = Revision,
                    ["timeoutMs"] = PollTimeoutMs,
                }, ct).ConfigureAwait(false);
                observation = SentioraObservation.From(result);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SentioraProtocolException ex) when (ex.Kind == SentioraErrorKind.UnknownSession)
            {
                // The host restarted, or the session was released underneath us. Every
                // id we hold is now meaningless; surface it as "backend lost" rather
                // than retrying against a runtime that will keep saying no.
                _backendLost = true;
                lock (_gate) _state = "released";
                return;
            }
            catch (SentioraProtocolException)
            {
                return;
            }
            catch (SentioraTransportException)
            {
                _backendLost = true;
                lock (_gate) _state = "exited";
                return;
            }
            catch (Exception)
            {
                // This loop is a background observer with nobody awaiting it, so an
                // unexpected exception would otherwise surface as an unobserved task
                // fault and leave a session that silently stopped updating. The honest
                // state after the observer dies is "no longer observable".
                _backendLost = true;
                lock (_gate) _state = "exited";
                return;
            }

            Apply(observation);

            if (observation.State is "exited" or "released")
            {
                // Docs promise the final screen stays readable after the child exits.
                // Give the emulator one more beat to flush trailing output, then take
                // a last observation and stop — an exited session makes terminal.wait
                // return immediately, which would otherwise spin.
                await CaptureFinalScreenAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task CaptureFinalScreenAsync()
    {
        try
        {
            await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
            await ObserveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best effort: the host may already be shutting down.
        }
    }

    private async Task ObserveAsync(CancellationToken ct)
    {
        var result = await _host.RequestAsync("terminal.observe",
            new JsonObject { ["sessionId"] = RuntimeSessionId }, ct).ConfigureAwait(false);
        Apply(SentioraObservation.From(result));
    }

    private void Apply(SentioraObservation observation)
    {
        lock (_gate)
        {
            // Revisions only move forward; a response that arrives after a newer one
            // must not drag the cached value back and re-trigger "Stale observation".
            if (observation.Revision > _revision) _revision = observation.Revision;
            _state = observation.State;
            if (observation.Cols > 0) _appliedCols = observation.Cols;
            if (observation.Rows > 0) _appliedRows = observation.Rows;
        }
        Paint(observation.Screen);
    }

    private long Revision { get { lock (_gate) return _revision; } }

    private void SetRevision(long revision)
    {
        lock (_gate) if (revision > _revision) _revision = revision;
    }

    /// <summary>
    /// Push the rendered screen into the caller's callback as a clear-and-repaint.
    /// This is the lossy step described on the class: it reproduces the VISIBLE grid
    /// and nothing else. Skipped when the screen has not changed, so an idle session
    /// emits no bytes at all.
    /// </summary>
    private void Paint(string screen)
    {
        lock (_gate)
        {
            if (screen == _lastPainted) return;
            _lastPainted = screen;
        }
        var payload = "\x1b[2J\x1b[H" + screen.Replace("\n", "\r\n");
        var bytes = Encoding.UTF8.GetBytes(payload);
        _onOutput(bytes, bytes.Length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotent
        _pollCancellation.Cancel();
        try
        {
            // Bounded: the poll loop wakes at most PollTimeoutMs after cancellation,
            // and waiting for it keeps a late Paint from racing teardown.
            _pollLoop.Wait(TimeSpan.FromMilliseconds(PollTimeoutMs * 4));
        }
        catch
        {
            // A faulted or cancelled poll loop is not a teardown failure.
        }

        if (!_backendLost && !_host.IsBroken)
        {
            try
            {
                // terminal.close requires the owner token; best effort, because the
                // runtime may already be gone and console_close must not hang on it.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _host.RequestAsync("terminal.close", new JsonObject
                {
                    ["sessionId"] = RuntimeSessionId,
                    ["owner"] = Owner,
                }, cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // The session is being torn down either way; the host reaps anything
                // left when its stdin closes.
            }
        }

        _host.ReleaseSession();
        _pollCancellation.Dispose();
        _writeGate.Dispose();
    }

    /// <summary>Exposed for tests: the state string the adapter is currently caching.</summary>
    internal string StateForTest { get { lock (_gate) return _state; } }

    /// <summary>Exposed for tests: the revision a subsequent write would present.</summary>
    internal long RevisionForTest => Revision;
}
