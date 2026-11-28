using Telekinesis.Abstractions;

namespace Telekinesis.Sentiora;

/// <summary>
/// Owns the Sentiora host processes behind the opt-in console backend (issue #67).
/// One host multiplexes many sessions over a single stdio pipe, but a host is a
/// consumable: it caps out at 16 concurrent sessions and degrades after roughly 360
/// create/close cycles because the runtime leaks a descriptor per session. So this
/// keeps a small pool, routes each console_open to a host that still has room, and
/// starts a replacement once the current one retires. Sessions cannot migrate between
/// hosts — a PTY is bound to the process that created it — so a retired host stays
/// alive until its own sessions drain, and is then reaped.
/// </summary>
internal sealed class SentioraBackend : IDisposable
{
    private readonly Func<SentioraLaunchSpec> _resolve;
    private readonly Func<SentioraLaunchSpec, ISentioraTransport> _transportFactory;
    private readonly Action<string> _announce;
    private readonly int _maxSessionsPerHost;
    private readonly int _recycleAfterCreates;
    private readonly List<SentioraHost> _hosts = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    /// <param name="resolve">Runtime discovery. Injectable so tests need no runtime installed.</param>
    /// <param name="transportFactory">Transport construction. Injectable so tests can fake the runtime.</param>
    /// <param name="maxSessionsPerHost">Override for the measured 16-session cap; tests only.</param>
    /// <param name="recycleAfterCreates">Override for the measured create-cycle ceiling; tests only.</param>
    /// <param name="announce">Where the "which backend is active" line goes. Defaults to
    /// stderr, which is where it must go — stdout is the MCP stream.</param>
    public SentioraBackend(
        Func<SentioraLaunchSpec>? resolve = null,
        Func<SentioraLaunchSpec, ISentioraTransport>? transportFactory = null,
        int maxSessionsPerHost = SentioraHost.MaxConcurrentSessions,
        int recycleAfterCreates = SentioraHost.RecycleAfterCreates,
        Action<string>? announce = null)
    {
        _resolve = resolve ?? (() => SentioraDiscovery.Resolve());
        _transportFactory = transportFactory ?? (spec => ProcessSentioraTransport.Start(spec));
        _maxSessionsPerHost = maxSessionsPerHost;
        _recycleAfterCreates = recycleAfterCreates;
        _announce = announce ?? (message => Console.Error.WriteLine(message));
    }

    /// <summary>Whether the opt-in switch is set. Off by default, so an untouched
    /// Telekinesis behaves exactly as it did before this backend existed.</summary>
    public static bool IsEnabled => SentioraDiscovery.IsEnabled();

    /// <summary>Hosts currently in the pool, including retired ones still draining.</summary>
    public int HostCount { get { lock (_hosts) return _hosts.Count; } }

    public IConsoleSession Open(string shell, int cols, int rows, Action<byte[], int> onOutput) =>
        // Sync-over-async at the boundary: ConsoleSessionService.Open is synchronous
        // and so is every existing backend's construction. There is no
        // SynchronizationContext here to deadlock against.
        OpenAsync(shell, cols, rows, onOutput).GetAwaiter().GetResult();

    public async Task<SentioraConsoleSession> OpenAsync(
        string shell, int cols, int rows, Action<byte[], int> onOutput, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // The gate is held for the whole open, including host startup, so two callers
        // cannot both decide the same host has room for a 17th session. Opening a
        // console session is not a hot path; correctness beats overlap here.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ReapDrainedHosts();

            SentioraHost? host;
            lock (_hosts) host = _hosts.Find(h => h.CanAcceptSession);
            if (host is null)
            {
                // Resolve per host, not once per process: a run that recycles hosts
                // should pick up a corrected TELEKINESIS_SENTIORA_CMD without a restart,
                // and a failure here must surface as a clear error rather than a silent
                // fallback (#67).
                var spec = _resolve();
                var transport = _transportFactory(spec);
                host = await SentioraHost.StartAsync(
                    transport, spec, _maxSessionsPerHost, _recycleAfterCreates, ct).ConfigureAwait(false);
                lock (_hosts) _hosts.Add(host);
                Announce(host);
            }

            return await SentioraConsoleSession.CreateAsync(host, shell, cols, rows, onOutput, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReapDrainedHosts()
    {
        lock (_hosts)
        {
            for (var i = _hosts.Count - 1; i >= 0; i--)
            {
                if (!_hosts[i].CanBeReaped) continue;
                _hosts[i].Dispose();
                _hosts.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Make the active backend unambiguous, which #67 requires if the two backends are
    /// to be compared experimentally. The default sink is stderr, never stdout: stdout
    /// IS the MCP stream, and a child's output reaching it is exactly the #46/#49
    /// failure mode. The handshake details are recorded so a run is attributable to a
    /// specific runtime, platform and ConPTY provider.
    /// </summary>
    private void Announce(SentioraHost host) => _announce(
        $"[telekinesis] console backend: Sentiora Terminal ({host.Handshake.Describe()}); "
        + $"runtime: {host.Spec.Describe()} (from {host.Spec.Source})");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_hosts)
        {
            foreach (var host in _hosts) host.Dispose();
            _hosts.Clear();
        }
        _gate.Dispose();
    }
}
