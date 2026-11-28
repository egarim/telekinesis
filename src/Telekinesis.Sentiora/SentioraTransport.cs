using System.Text.Json.Nodes;

namespace Telekinesis.Sentiora;

/// <summary>
/// The seam between the adapter and the runtime: one JSON-RPC line transport to one
/// Sentiora host process. Tests substitute an in-memory fake so the adapter's
/// revision, ownership, clamping and recycling logic is exercised without Node being
/// installed; <see cref="ProcessSentioraTransport"/> is the real one.
/// </summary>
internal interface ISentioraTransport : IDisposable
{
    /// <summary>Send a request and await its response.</summary>
    /// <returns>The <c>result</c> node.</returns>
    /// <exception cref="SentioraProtocolException">The runtime answered with an error.</exception>
    /// <exception cref="SentioraTransportException">The pipe broke, or a line did not parse.</exception>
    Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct = default);

    /// <summary>True once this transport can no longer serve requests.</summary>
    bool IsBroken { get; }

    /// <summary>Why it broke; null while healthy.</summary>
    string? BrokenReason { get; }
}

/// <summary>
/// The real transport: spawn the runtime with piped stdio, write one JSON object per
/// line, read stdout line by line and parse every line (issue #67).
/// </summary>
internal sealed class ProcessSentioraTransport : ISentioraTransport
{
    private readonly System.Diagnostics.Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<SentioraResponse>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _nextId;
    private int _broken;
    private string? _brokenReason;

    public SentioraLaunchSpec Spec { get; }

    /// <summary>The runtime's own stderr, capped. Kept for diagnostics only: it is
    /// never forwarded to our stdout, because Telekinesis's MCP stream lives there
    /// and #46 is precisely what happens when a child's output reaches it.</summary>
    public string StderrTail { get; private set; } = string.Empty;

    private ProcessSentioraTransport(System.Diagnostics.Process process, SentioraLaunchSpec spec)
    {
        _process = process;
        Spec = spec;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(StderrLoopAsync);
    }

    public static ProcessSentioraTransport Start(SentioraLaunchSpec spec)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // CreateNoWindow matters: without it the runtime gets a console, and a
            // console-attached child is how #46/#49's "renders to the parent console"
            // failure mode reaches Telekinesis's own stdout.
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        foreach (var arg in spec.Arguments) psi.ArgumentList.Add(arg);

        // The runtime writes UTF-8 JSON that does not escape non-ASCII, so both pipes
        // must be UTF-8 regardless of the console's code page.
        psi.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
        psi.StandardErrorEncoding = new System.Text.UTF8Encoding(false);
        psi.StandardInputEncoding = new System.Text.UTF8Encoding(false);

        // Environment inheritance is deliberate and matches the existing native
        // backends, which copy the whole parent environment into the PTY child
        // (UnixPtyConsoleSession.BuildEnvp). Sentiora offers no protocol-level
        // environment override, so whatever is in THIS process is visible to every
        // session it hosts — see docs/CONSOLE.md#security. Not a regression, but not
        // a mitigation either.
        var process = new System.Diagnostics.Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                throw new SentioraNotFoundException($"the Sentiora runtime \"{spec.Describe()}\" did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            process.Dispose();
            throw new SentioraNotFoundException(
                $"the Sentiora runtime \"{spec.Describe()}\" could not be launched: {ex.Message}");
        }
        return new ProcessSentioraTransport(process, spec);
    }

    public bool IsBroken => Volatile.Read(ref _broken) != 0;
    public string? BrokenReason => _brokenReason;

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct = default)
    {
        if (IsBroken) throw new SentioraTransportException(_brokenReason ?? "The Sentiora runtime is gone.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<SentioraResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var payload = SentioraFraming.FormatRequest(id, method, parameters);
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Serialised: two concurrent WriteLine calls can interleave and put
                // half a JSON object on the wire, which the runtime would reject and
                // we would then have no way to correlate.
                await _stdin.WriteAsync(payload).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            Break($"writing to the Sentiora runtime failed: {ex.Message}");
            throw new SentioraTransportException($"Writing to the Sentiora runtime failed: {ex.Message}");
        }

        // There is no cancellation primitive in the protocol: an in-flight request
        // cannot be aborted server-side, it completes or times out. Cancelling here
        // only stops US waiting; the response still arrives later and is dropped
        // because its id is no longer pending. Callers use short timeouts so the
        // granularity of that is ~250 ms rather than unbounded.
        using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            SentioraResponse response;
            try
            {
                response = await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _pending.TryRemove(id, out _);
                throw;
            }
            if (response.IsError) throw SentioraErrors.ToException(response.ErrorMessage ?? string.Empty, response.ErrorCode);
            return response.Result;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _stdout.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                SentioraResponse response;
                try
                {
                    response = SentioraFraming.ParseLine(line);
                }
                catch (SentioraTransportException ex)
                {
                    Break(ex.Message);
                    return;
                }
                // A response whose id is not pending was already abandoned (cancelled
                // or timed out). Dropping it is correct, not silent data loss.
                if (_pending.TryRemove(response.Id, out var tcs)) tcs.TrySetResult(response);
            }
            Break("the Sentiora runtime closed its stdout");
        }
        catch (Exception ex)
        {
            Break($"reading the Sentiora runtime failed: {ex.Message}");
        }
    }

    private async Task StderrLoopAsync()
    {
        const int cap = 4000;
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                StderrTail = (StderrTail + line + "\n").Length <= cap
                    ? StderrTail + line + "\n"
                    : StderrTail[^Math.Min(StderrTail.Length, cap / 2)..] + line + "\n";
        }
        catch
        {
            // Diagnostics only; a stderr read failure must never take the transport down.
        }
    }

    private void Break(string reason)
    {
        if (Interlocked.Exchange(ref _broken, 1) != 0) return;
        _brokenReason = reason;
        var failure = new SentioraTransportException(reason);
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(failure);
    }

    public void Dispose()
    {
        Break("the Sentiora runtime was shut down");
        try
        {
            // Closing stdin is the graceful shutdown signal: the runtime reaps its
            // sessions and exits. Measured at ~5.0 s, so allow 6 s before force-killing.
            try { _stdin.Flush(); _stdin.Close(); } catch { }
            if (!_process.WaitForExit(6000)) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            _process.Dispose();
            _writeGate.Dispose();
        }
    }
}
