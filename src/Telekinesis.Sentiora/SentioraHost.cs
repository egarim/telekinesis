using System.Text.Json.Nodes;

namespace Telekinesis.Sentiora;

/// <summary>What <c>initialize</c> returns. Recorded so a run is attributable to the
/// exact runtime configuration that served it (issue #67).</summary>
internal sealed record SentioraHandshake(int ProtocolVersion, string Platform, string Arch, bool ConptyDll)
{
    public string Describe() =>
        $"protocol v{ProtocolVersion}, platform={Platform}, arch={Arch}, conptyDll={(ConptyDll ? "true" : "false")}";
}

/// <summary>A decoded observation — the shape returned by terminal.create,
/// terminal.observe, terminal.wait and terminal.resize.</summary>
internal readonly record struct SentioraObservation(
    string SessionId,
    string State,
    long Revision,
    bool Running,
    int Cols,
    int Rows,
    string Screen,
    int WriteErrors)
{
    public static SentioraObservation From(JsonNode? node)
    {
        if (node is not JsonObject o)
            throw new SentioraTransportException("The Sentiora runtime returned a non-object observation.");
        return new SentioraObservation(
            String(o, "sessionId"),
            // `state` is preferred over `running`: it distinguishes "exited" (child gone,
            // resources held) from "released" (close() done), which `running` collapses.
            String(o, "state"),
            Long(o, "revision"),
            o["running"] is JsonValue rv && rv.TryGetValue<bool>(out var running) && running,
            Int(o, "cols"),
            Int(o, "rows"),
            String(o, "screen"),
            Int(o, "writeErrors"));

        static string String(JsonObject o, string n) =>
            o[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
        static long Long(JsonObject o, string n) =>
            o[n] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
        static int Int(JsonObject o, string n) =>
            o[n] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    }
}

/// <summary>
/// One Sentiora host process. A host is NOT immortal: the runtime leaks roughly one
/// file descriptor and 1.75 Win32 handles per session and stops accepting new ones at
/// about 500 create/close cycles, with degradation measured from ~363. So each host
/// counts the sessions it has created and retires itself before that ceiling, and
/// <see cref="SentioraBackend"/> starts a replacement. It also enforces the runtime's
/// 16-concurrent-session cap locally, so a caller gets a clear "start another host"
/// path instead of the runtime's bare "Session limit".
/// </summary>
internal sealed class SentioraHost : IDisposable
{
    /// <summary>The runtime's hard concurrent-session cap, measured.</summary>
    public const int MaxConcurrentSessions = 16;

    /// <summary>Create count at which a host stops accepting new sessions. Below the
    /// measured ~363 degradation point, so a host is retired while it is still healthy
    /// rather than after it starts failing.</summary>
    public const int RecycleAfterCreates = 360;

    /// <summary>The protocol version this adapter was written against.</summary>
    public const int SupportedProtocolVersion = 1;

    private readonly ISentioraTransport _transport;
    private readonly int _maxConcurrentSessions;
    private readonly int _recycleAfterCreates;
    private int _creates;
    private int _live;
    private int _disposed;

    public SentioraHandshake Handshake { get; }
    public SentioraLaunchSpec Spec { get; }

    /// <summary>How many sessions this host has created since it started — the counter
    /// that drives recycling, since the leak is per create, not per live session.</summary>
    public int CreateCount => Volatile.Read(ref _creates);

    public int LiveSessions => Volatile.Read(ref _live);

    /// <summary>Retired once it has served its quota of creates, so it takes no new
    /// sessions but keeps serving the ones it has until they drain.</summary>
    public bool IsRetired => CreateCount >= _recycleAfterCreates;

    public bool IsBroken => _transport.IsBroken;

    /// <summary>A drained, retired (or broken) host can be shut down.</summary>
    public bool CanBeReaped => LiveSessions == 0 && (IsRetired || IsBroken || _disposed != 0);

    public bool CanAcceptSession => !IsBroken && _disposed == 0 && LiveSessions < _maxConcurrentSessions && !IsRetired;

    private SentioraHost(
        ISentioraTransport transport, SentioraHandshake handshake, SentioraLaunchSpec spec,
        int maxConcurrentSessions, int recycleAfterCreates)
    {
        _transport = transport;
        Handshake = handshake;
        Spec = spec;
        _maxConcurrentSessions = maxConcurrentSessions;
        _recycleAfterCreates = recycleAfterCreates;
    }

    /// <summary>Wrap a transport and handshake it. Throws unless the runtime speaks the
    /// protocol version this adapter was written against — a silent version skew here
    /// would show up later as bizarre per-call failures. The two limits default to the
    /// measured values and are overridable only so tests can reach the recycling path
    /// without creating 360 real sessions.</summary>
    public static async Task<SentioraHost> StartAsync(
        ISentioraTransport transport,
        SentioraLaunchSpec spec,
        int maxConcurrentSessions = MaxConcurrentSessions,
        int recycleAfterCreates = RecycleAfterCreates,
        CancellationToken ct = default)
    {
        JsonNode? result;
        try
        {
            result = await transport.RequestAsync("initialize", [], ct).ConfigureAwait(false);
        }
        catch
        {
            transport.Dispose();
            throw;
        }

        var handshake = result is JsonObject o
            ? new SentioraHandshake(
                o["protocolVersion"] is JsonValue pv && pv.TryGetValue<int>(out var v) ? v : -1,
                o["platform"] is JsonValue pf && pf.TryGetValue<string>(out var p) ? p : "?",
                o["arch"] is JsonValue ar && ar.TryGetValue<string>(out var a) ? a : "?",
                o["conptyDll"] is JsonValue cd && cd.TryGetValue<bool>(out var c) && c)
            : throw new SentioraTransportException("The Sentiora runtime returned no handshake.");

        if (handshake.ProtocolVersion != SupportedProtocolVersion)
        {
            transport.Dispose();
            throw new SentioraTransportException(
                $"The Sentiora runtime speaks protocol v{handshake.ProtocolVersion}, but this build of "
                + $"Telekinesis supports v{SupportedProtocolVersion}. Update one of them.");
        }

        return new SentioraHost(transport, handshake, spec, maxConcurrentSessions, recycleAfterCreates);
    }

    public Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct = default) =>
        _transport.RequestAsync(method, parameters, ct);

    /// <summary>Reserve a slot before terminal.create so two concurrent opens cannot
    /// both decide the host has room.</summary>
    public bool TryReserveSession()
    {
        while (true)
        {
            var live = Volatile.Read(ref _live);
            if (live >= _maxConcurrentSessions || IsRetired || IsBroken || _disposed != 0) return false;
            if (Interlocked.CompareExchange(ref _live, live + 1, live) == live)
            {
                Interlocked.Increment(ref _creates);
                return true;
            }
        }
    }

    public void ReleaseSession() => Interlocked.Decrement(ref _live);

    /// <summary>Give back a reservation whose terminal.create never succeeded, without
    /// counting it as a create cycle.</summary>
    public void UnreserveSession()
    {
        Interlocked.Decrement(ref _live);
        Interlocked.Decrement(ref _creates);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _transport.Dispose();
    }
}
