using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Telekinesis.Cli;

/// <summary>One attachable CDP target from /json/list.</summary>
public sealed record CdpTarget(string Id, string Type, string Title, string Url, string? WebSocketDebuggerUrl);

/// <summary>
/// The optional Chrome DevTools Protocol tier (issue #51): console, network
/// metadata and JS evaluation for a browser that was started with
/// --remote-debugging-port. OFF unless TELEKINESIS_CDP=1, loopback only, and no
/// new dependencies — CDP is JSON over a WebSocket, so ClientWebSocket plus
/// System.Text.Json is the whole transport.
///
/// DI singleton; owns one <see cref="CdpSession"/> per attached target, which
/// live until the process exits.
/// </summary>
public sealed class CdpSessionService : IAsyncDisposable
{
    public const string EnabledEnvVar = "TELEKINESIS_CDP";
    public const string PortEnvVar = "TELEKINESIS_CDP_PORT";

    /// <summary>The tier is opt-in: nothing connects and no tools load without it.</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable(EnabledEnvVar) is "1" or "true";

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable(PortEnvVar), out var p) && p is > 0 and <= 65535
            ? p : 9222;

    /// <summary>Loopback is not configurable — there is no host knob to get wrong.</summary>
    public static Uri Endpoint => new($"http://127.0.0.1:{Port}");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CdpSession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>The browser's version string, or null when nothing answers.</summary>
    public static async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                await Http.GetStringAsync(new Uri(Endpoint, "/json/version"), ct));
            return doc.RootElement.TryGetProperty("Browser", out var b) ? b.GetString() : "unknown";
        }
        catch { return null; }
    }

    public static async Task<IReadOnlyList<CdpTarget>> ListTargetsAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(new Uri(Endpoint, "/json/list"), ct));
        var targets = new List<CdpTarget>();
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            string? S(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = S("id");
            if (id is null) continue;
            targets.Add(new CdpTarget(
                id, S("type") ?? "other",
                // A page title is page-authored text — scrub it like any other.
                CdpFormat.Safe(S("title")),
                CdpFormat.ProjectUrl(S("url")),
                S("webSocketDebuggerUrl"))); // may be absent — callers must handle null
        }
        return targets;
    }

    /// <summary>
    /// Attach to a page target (cached). Empty id picks the single page target,
    /// erroring when the choice is ambiguous rather than guessing.
    /// </summary>
    internal async Task<CdpSession> AttachAsync(string? targetId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enabled)
            throw new InvalidOperationException(
                $"The CDP tier is off. Set {EnabledEnvVar}=1 and start the browser with "
                + $"--remote-debugging-port={Port} (docs/BROWSERS.md).");

        var targets = await ListTargetsAsync(ct);
        var pages = targets.Where(t => t.Type == "page" && t.WebSocketDebuggerUrl is not null).ToList();
        CdpTarget target;
        if (!string.IsNullOrEmpty(targetId))
        {
            target = targets.FirstOrDefault(t => t.Id == targetId)
                ?? throw new KeyNotFoundException($"No CDP target '{targetId}' (browser_targets lists current ones).");
            if (target.WebSocketDebuggerUrl is null)
                throw new InvalidOperationException($"Target '{targetId}' exposes no debugger socket.");
        }
        else
        {
            target = pages.Count switch
            {
                1 => pages[0],
                0 => throw new InvalidOperationException(
                    $"No page target at {Endpoint}. Open a tab, or start the browser with --remote-debugging-port={Port}."),
                _ => throw new InvalidOperationException(
                    $"{pages.Count} page targets are open — pass targetId (browser_targets lists them)."),
            };
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(target.Id, out var existing))
            {
                if (existing.IsAlive) return existing;
                // A closed tab's session must be disposed, not merely replaced —
                // otherwise the table grows with every tab ever attached.
                _sessions.Remove(target.Id);
                await existing.DisposeAsync();
            }
            foreach (var (id, dead) in _sessions.Where(kv => !kv.Value.IsAlive).ToList())
            {
                _sessions.Remove(id);
                await dead.DisposeAsync();
            }
            var session = await CdpSession.ConnectAsync(target, ct);
            _sessions[target.Id] = session;
            // Attaching IS the grant — the one read-path event worth auditing.
            AuditLog.Append("browser_attach", $"port={Port} target={target.Url}", true, "cdp");
            return session;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        List<CdpSession> live;
        await _gate.WaitAsync();
        try { live = [.. _sessions.Values]; _sessions.Clear(); }
        finally { _gate.Release(); }
        foreach (var s in live) await s.DisposeAsync();
        _gate.Dispose();
    }
}

/// <summary>
/// One WebSocket to one CDP target, with a background receive pump feeding
/// bounded console/network rings.
///
/// This session sends EXACTLY four methods, and a reviewer can verify that by
/// reading this comment: Runtime.enable, Log.enable, Network.enable, and
/// Runtime.evaluate (browser_evaluate only). It never calls
/// Network.getResponseBody, getRequestPostData, Page.getResourceContent,
/// Network.getCookies/getAllCookies, Storage.*, DOMStorage.* or IndexedDB.* —
/// so response bodies, request bodies and cookies cannot leave the browser.
/// </summary>
internal sealed class CdpSession : IAsyncDisposable
{
    private readonly ClientWebSocket? _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _send = new(1, 1);   // one SendAsync at a time — concurrent sends interleave frames
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Dictionary<string, NetEntry> _inFlight = new(StringComparer.Ordinal);
    private readonly object _netGate = new();
    private volatile Exception? _fault;
    private int _nextId;

    public string TargetId { get; }
    public string Url { get; }
    public Ring<ConsoleEntry> Console { get; } = new(200);
    public Ring<NetEntry> Network { get; } = new(500);
    public bool IsAlive => _fault is null && _socket?.State == WebSocketState.Open;

    /// <summary>Test seam: a session with no socket, for driving Ingest/Fault directly.</summary>
    internal CdpSession(string targetId, string url)
    {
        TargetId = targetId;
        Url = url;
    }

    /// <summary>Test seam: register a pending call without touching a socket.</summary>
    internal void RegisterForTest(int id, TaskCompletionSource<JsonElement> tcs) => _pending[id] = tcs;

    private CdpSession(CdpTarget target, ClientWebSocket socket)
    {
        TargetId = target.Id;
        Url = target.Url;
        _socket = socket;
    }

    internal static async Task<CdpSession> ConnectAsync(CdpTarget target, CancellationToken ct)
    {
        var wsUrl = target.WebSocketDebuggerUrl
            ?? throw new InvalidOperationException("Target exposes no debugger socket.");
        // The browser echoes back the host it was addressed by; a doctored
        // /json/list must not be able to redirect us off the loopback interface.
        if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri) || !IsLoopback(uri.Host))
            throw new InvalidOperationException($"Refusing a non-loopback CDP socket: {wsUrl}");

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // No Origin header: Chrome >= 111 rejects the upgrade unless it matches
        // --remote-allow-origins.
        await socket.ConnectAsync(uri, ct);

        var session = new CdpSession(target, socket);
        _ = Task.Run(session.PumpAsync);
        try
        {
            // Runtime/Log replay the browser's buffered history on enable; Network
            // does NOT, so traffic is only captured from this moment on.
            await session.CallAsync("Runtime.enable", null, ct);
            await session.CallAsync("Log.enable", null, ct);
            await session.CallAsync("Network.enable",
                new { maxTotalBufferSize = 10_000_000, maxResourceBufferSize = 1_000_000 }, ct);
        }
        catch
        {
            // A half-enabled session is never handed out — tear the socket and pump
            // down rather than leaking them outside the service's session table.
            await session.DisposeAsync();
            throw;
        }
        return session;
    }

    private static bool IsLoopback(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    /// <summary>Send a CDP command and await its reply.</summary>
    public async Task<JsonElement> CallAsync(string method, object? @params, CancellationToken ct)
    {
        if (_fault is { } dead) throw new InvalidOperationException($"CDP session is closed: {dead.Message}", dead);
        if (_socket is null) throw new InvalidOperationException("This session has no socket (test seam).");

        var id = Interlocked.Increment(ref _nextId);
        // RunContinuationsAsynchronously: without it the awaiting caller resumes ON
        // the pump thread, leaving no pending read on the socket.
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        if (_fault is { } raced) { _pending.TryRemove(id, out _); throw new InvalidOperationException("CDP session is closed.", raced); }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            @params is null ? new { id, method } : (object)new { id, method, @params });
        try
        {
            await _send.WaitAsync(ct);
            try
            {
                // A per-request token must never reach the socket — cancelling a
                // send Abort()s the connection and kills every other in-flight
                // call. A stalled send is a dead session, so bound it at the
                // SESSION level and fault everyone rather than holding the gate.
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                sendCts.CancelAfter(TimeSpan.FromSeconds(10));
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, sendCts.Token);
            }
            finally { _send.Release(); }
        }
        catch (Exception ex)
        {
            // Never leave a registration behind for a request that never went out.
            _pending.TryRemove(id, out _);
            if (ex is not OperationCanceledException || _cts.IsCancellationRequested) Fault(ex);
            throw;
        }

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[32 * 1024];
        var message = new ArrayBufferWriter<byte>(64 * 1024);
        try
        {
            while (_socket!.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                message.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Fault(new IOException("The browser closed the CDP socket."));
                        return;
                    }
                    message.Write(buffer.AsSpan(0, result.Count));
                    // EndOfMessage is false both when the peer fragments AND when our
                    // buffer simply filled — accumulate unconditionally.
                } while (!result.EndOfMessage);

                try
                {
                    using var doc = JsonDocument.Parse(message.WrittenMemory);
                    Ingest(doc.RootElement);
                }
                catch (JsonException) { /* a frame we can't parse is not fatal */ }
            }
            Fault(new IOException("The CDP socket closed."));
        }
        catch (Exception ex) { Fault(ex); }
    }

    /// <summary>
    /// Route one CDP frame: a reply (has "id") completes its pending call, an
    /// event (has "method") lands in a ring. Test seam — no socket involved.
    /// </summary>
    internal void Ingest(JsonElement root)
    {
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
        {
            if (!_pending.TryRemove(id, out var tcs)) return;
            if (root.TryGetProperty("error", out var err))
                // Scrubbed like any other page-influenced text — CdpFormat is the
                // single boundary, so the error channel must not bypass it.
                tcs.TrySetException(new InvalidOperationException($"CDP error: {CdpFormat.Safe(err.ToString())}"));
            else
                // Clone: JsonDocument.Parse over the pump's reused buffer does not copy.
                tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
            return;
        }

        if (!root.TryGetProperty("method", out var m) || m.GetString() is not { } method) return;
        var p = root.TryGetProperty("params", out var pe) ? pe : default;

        switch (method)
        {
            case "Runtime.consoleAPICalled": OnConsole(p); break;
            case "Runtime.exceptionThrown": OnException(p); break;
            case "Log.entryAdded": OnLogEntry(p); break;
            case "Network.requestWillBeSent": OnRequest(p); break;
            case "Network.responseReceived": OnResponse(p); break;
            case "Network.loadingFinished": OnFinished(p); break;
            case "Network.loadingFailed": OnFailed(p); break;
        }
    }

    private void OnConsole(JsonElement p)
    {
        var type = Str(p, "type");
        // ponytail: %s/%c format specifiers are left intact — Chrome does not
        // substitute them for us, and readable-enough beats a formatter.
        var text = new StringBuilder();
        if (p.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            foreach (var a in args.EnumerateArray())
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(CdpFormat.Flatten(a));
            }
        var (url, line) = FirstFrame(p);
        Console.Add(new ConsoleEntry(CdpFormat.Level(type), type ?? "console",
            CdpFormat.Safe(text.ToString()), url, line, RuntimeTs(p)));
    }

    private void OnException(JsonElement p)
    {
        string? text = null;
        string? url = null;
        int? line = null;
        if (p.TryGetProperty("exceptionDetails", out var d))
        {
            // .text is almost always the useless literal "Uncaught" — the real
            // message lives on the thrown object's description.
            if (d.TryGetProperty("exception", out var ex))
                text = Str(ex, "description") ?? Str(ex, "className");
            text ??= Str(d, "text");
            url = CdpFormat.ProjectUrl(Str(d, "url"));
            if (d.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l)) line = l + 1; // CDP is 0-based
        }
        Console.Add(new ConsoleEntry("error", "exception", CdpFormat.Safe(text), NullIfEmpty(url), line, RuntimeTs(p)));
    }

    private void OnLogEntry(JsonElement p)
    {
        if (!p.TryGetProperty("entry", out var e)) return;
        int? line = e.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? l + 1 : null;
        Console.Add(new ConsoleEntry(
            CdpFormat.Level(Str(e, "level")), Str(e, "source") ?? "log",
            CdpFormat.Safe(Str(e, "text")), NullIfEmpty(CdpFormat.ProjectUrl(Str(e, "url"))), line,
            RuntimeTs(e)));
    }

    private void OnRequest(JsonElement p)
    {
        var requestId = Str(p, "requestId");
        if (requestId is null) return;
        // request.headers and request.postData arrive uninvited — the login POST
        // with the cleartext password is in this very payload. Not reading those
        // properties is the fix; "we never call getRequestPostData" is not.
        var req = p.TryGetProperty("request", out var r) ? r : default;
        var entry = new NetEntry
        {
            RequestId = requestId,
            Method = Str(req, "method") ?? "GET",
            Url = CdpFormat.ProjectUrl(Str(req, "url")),
            ResourceType = Str(p, "type") ?? "Other",
            // wallTime is seconds since the epoch; timestamp is MonotonicTime
            // (seconds, arbitrary origin) and only good for deltas.
            Ts = p.TryGetProperty("wallTime", out var w) && w.TryGetDouble(out var wall)
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(wall * 1000))
                : DateTimeOffset.Now,
            StartMonotonic = Num(p, "timestamp"),
        };
        lock (_netGate)
        {
            // A redirect re-fires requestWillBeSent with the SAME requestId, and it
            // carries the PREVIOUS hop's response as redirectResponse — the only
            // place that 3xx ever appears. Close out the old hop before replacing it,
            // or it is left forever with a null status.
            if (p.TryGetProperty("redirectResponse", out var redirect)
                && _inFlight.TryGetValue(requestId, out var previous))
            {
                ApplyResponse(previous, redirect);
                Complete(previous, p);
            }
            // Keep both hops as separate entries; point the correlation at the newest.
            var evicted = Network.Add(entry);
            if (evicted is not null && _inFlight.TryGetValue(evicted.RequestId, out var mapped)
                && ReferenceEquals(mapped, evicted))
                _inFlight.Remove(evicted.RequestId);
            _inFlight[requestId] = entry;
        }
    }

    private void OnResponse(JsonElement p)
    {
        var res = p.TryGetProperty("response", out var r) ? r : default;
        WithEntry(p, e => ApplyResponse(e, res));
    }

    /// <summary>Copy response METADATA onto an entry. response.headers is never read.</summary>
    private static void ApplyResponse(NetEntry e, JsonElement res)
    {
        if (res.ValueKind != JsonValueKind.Object) return;
        if (res.TryGetProperty("status", out var s) && s.TryGetInt32(out var status)) e.Status = status;
        e.MimeType = Str(res, "mimeType");
        e.FromCache = res.TryGetProperty("fromDiskCache", out var c) && c.ValueKind == JsonValueKind.True;
    }

    private void OnFinished(JsonElement p) => WithEntry(p, e =>
    {
        // responseReceived.encodedDataLength is bytes-so-far (usually just headers);
        // the real size only arrives here.
        var size = Num(p, "encodedDataLength");
        if (size > 0) e.Size = (long)size;
        Complete(e, p);
    });

    private void OnFailed(JsonElement p) => WithEntry(p, e =>
    {
        e.Failed = CdpFormat.Safe(Str(p, "errorText") ?? "failed");
        Complete(e, p);
    });

    private static void Complete(NetEntry e, JsonElement p)
    {
        var end = Num(p, "timestamp");
        if (end > 0 && e.StartMonotonic > 0) e.DurationMs = Math.Round((end - e.StartMonotonic) * 1000, 1);
    }

    private void WithEntry(JsonElement p, Action<NetEntry> update)
    {
        var requestId = Str(p, "requestId");
        if (requestId is null) return;
        lock (_netGate)
            if (_inFlight.TryGetValue(requestId, out var entry)) update(entry);
    }

    private static (string? Url, int? Line) FirstFrame(JsonElement p)
    {
        if (p.TryGetProperty("stackTrace", out var st) &&
            st.TryGetProperty("callFrames", out var frames) &&
            frames.ValueKind == JsonValueKind.Array)
            foreach (var f in frames.EnumerateArray())
            {
                var url = CdpFormat.ProjectUrl(Str(f, "url"));
                int? line = f.TryGetProperty("lineNumber", out var ln) && ln.TryGetInt32(out var l) ? l + 1 : null;
                return (NullIfEmpty(url), line);
            }
        return (null, null);
    }

    /// <summary>Runtime timestamps are MILLISECONDS since the epoch (Network's are not).</summary>
    private static DateTimeOffset RuntimeTs(JsonElement p) =>
        p.TryGetProperty("timestamp", out var t) && t.TryGetDouble(out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : DateTimeOffset.Now;

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double Num(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Kill the session and fail every waiting caller — never let one hang.</summary>
    internal void Fault(Exception ex)
    {
        _fault ??= ex;   // set BEFORE draining so a racing CallAsync sees it
        // Cancelling the session token aborts a blocked SendAsync, whose finally
        // releases the send gate — otherwise a caller parked INSIDE the send (not
        // yet in _pending) would wait for the full send timeout, and everyone
        // queued behind the gate with it.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already torn down */ }
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(new InvalidOperationException("CDP session closed.", ex));
    }

    public async ValueTask DisposeAsync()
    {
        Fault(new ObjectDisposedException(nameof(CdpSession)));
        await _cts.CancelAsync();
        if (_socket is not null)
        {
            try
            {
                // CloseAsync waits for the peer's close frame and hangs forever on a
                // crashed browser; announce and abort instead.
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, quick.Token);
            }
            catch { /* best effort */ }
            _socket.Abort();
            _socket.Dispose();
        }
        _cts.Dispose();
        _send.Dispose();
    }
}

/// <summary>A bounded newest-wins buffer. Add returns the evicted item, so the
/// caller can drop any index that pointed at it.</summary>
internal sealed class Ring<T>(int capacity) where T : class
{
    private readonly T?[] _items = new T[capacity];
    private readonly object _gate = new();
    private int _head;
    private int _count;

    public int Count { get { lock (_gate) return _count; } }

    public T? Add(T item)
    {
        lock (_gate)
        {
            var evicted = _items[_head];
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
            return _count == _items.Length ? evicted : null;
        }
    }

    /// <summary>Newest first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<T> Snapshot(int max)
    {
        lock (_gate)
        {
            var take = Math.Min(max <= 0 ? _count : max, _count);
            var result = new List<T>(take);
            for (var i = 0; i < take; i++)
            {
                var index = (_head - 1 - i + _items.Length * 2) % _items.Length;
                if (_items[index] is { } item) result.Add(item);
            }
            return result;
        }
    }
}

internal sealed record ConsoleEntry(string Level, string Source, string Text, string? Url, int? Line, DateTimeOffset Ts);

/// <summary>Metadata only — no headers, no body, ever.</summary>
internal sealed class NetEntry
{
    public required string RequestId { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required string ResourceType { get; init; }
    public int? Status { get; set; }
    public string? MimeType { get; set; }
    public long? Size { get; set; }
    public double? DurationMs { get; set; }
    public bool FromCache { get; set; }
    public string? Failed { get; set; }
    public DateTimeOffset Ts { get; init; }
    internal double StartMonotonic { get; init; }
}
