using Telekinesis.Sentiora;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// Host lifecycle (issue #67). A Sentiora host process is a consumable, not a
/// singleton: it caps out at 16 concurrent sessions and degrades after roughly 360
/// create/close cycles because the runtime leaks a descriptor per session and starts
/// failing with "Invalid pty handle" near 500. These tests pin the recycling and
/// pooling behaviour that keeps a long-lived Telekinesis server working past both
/// limits. Driven by <see cref="FakeSentioraTransport"/> — no Node.js required.
/// </summary>
public class SentioraBackendTests
{
    private static readonly Action<byte[], int> NoSink = (_, _) => { };

    private static SentioraLaunchSpec FakeSpec() => new("fake-runtime", [], "test");

    [Fact]
    public async Task The_handshake_records_platform_arch_and_conptyDll_so_a_run_is_attributable()
    {
        var fake = new FakeSentioraTransport();
        using var host = await SentioraHost.StartAsync(fake, FakeSpec());

        Assert.Equal(SentioraHost.SupportedProtocolVersion, host.Handshake.ProtocolVersion);
        Assert.Equal("fakeos", host.Handshake.Platform);
        Assert.Equal("fakearch", host.Handshake.Arch);
        Assert.False(host.Handshake.ConptyDll);
        Assert.Contains("platform=fakeos", host.Handshake.Describe());
        Assert.Contains("arch=fakearch", host.Handshake.Describe());
        Assert.Contains("conptyDll=false", host.Handshake.Describe());
    }

    [Fact]
    public async Task A_protocol_version_mismatch_is_refused_before_any_session_is_created()
    {
        // A silent version skew would surface later as inexplicable per-call failures,
        // so it is rejected at handshake and the transport is cleaned up.
        var fake = new FakeSentioraTransport { ProtocolVersion = 2 };
        var ex = await Assert.ThrowsAsync<SentioraTransportException>(
            () => SentioraHost.StartAsync(fake, FakeSpec()));

        Assert.Contains("protocol v2", ex.Message);
        Assert.Contains($"v{SentioraHost.SupportedProtocolVersion}", ex.Message);
        Assert.True(fake.Disposed, "a refused host must not be left running");
    }

    [Fact]
    public async Task Starting_a_host_announces_the_active_backend_on_stderr()
    {
        var captured = new StringWriter();
        var original = Console.Error;
        Console.SetError(captured);
        try
        {
            using var backend = new SentioraBackend(
                resolve: FakeSpec, transportFactory: _ => new FakeSentioraTransport());
            using var session = await backend.OpenAsync("sh", 80, 24, NoSink);
        }
        finally
        {
            Console.SetError(original);
        }

        // #67 requires it to be unambiguous which backend is active, and stdout is the
        // MCP stream — so the announcement has to be on stderr.
        var message = captured.ToString();
        Assert.Contains("console backend: Sentiora Terminal", message);
        Assert.Contains("platform=fakeos", message);
        Assert.Contains("conptyDll=false", message);
        Assert.Contains("fake-runtime", message);
    }

    [Fact]
    public async Task A_host_is_retired_after_its_create_quota_and_a_replacement_is_started()
    {
        using var harness = new SentioraHarness(recycleAfterCreates: 2);

        using var first = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        using var second = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        Assert.Single(harness.Transports);

        // The third session must land on a fresh host: the first has served its quota
        // and is retired while still healthy, rather than being used until it breaks.
        using var third = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        Assert.Equal(2, harness.Transports.Count);
        Assert.Equal(2, harness.Backend.HostCount);
        Assert.False(harness.Transports[0].Disposed, "a retired host with live sessions must keep serving them");
        Assert.Equal(1, harness.Transports[1].SessionCount);
    }

    [Fact]
    public async Task A_retired_host_is_reaped_once_its_sessions_drain()
    {
        using var harness = new SentioraHarness(recycleAfterCreates: 1);

        var first = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        Assert.Single(harness.Transports);
        first.Dispose();

        // A PTY cannot migrate between hosts, so retirement is generational: the old
        // host stays until it drains, then goes away instead of accumulating forever.
        using var second = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        Assert.Equal(2, harness.Transports.Count);
        Assert.True(harness.Transports[0].Disposed, "a drained, retired host should be shut down");
        Assert.Equal(1, harness.Backend.HostCount);
    }

    [Fact]
    public async Task Sessions_past_the_per_host_cap_start_another_host_instead_of_failing()
    {
        using var harness = new SentioraHarness(maxSessionsPerHost: 1);

        using var first = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        using var second = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);

        // The runtime's cap is enforced locally, so a caller gets another host rather
        // than the runtime's bare "Session limit".
        Assert.Equal(2, harness.Transports.Count);
        Assert.Equal(1, harness.Transports[0].SessionCount);
        Assert.Equal(1, harness.Transports[1].SessionCount);
    }

    [Fact]
    public async Task The_runtime_session_limit_is_mapped_to_an_actionable_message()
    {
        var fake = new FakeSentioraTransport { SessionLimit = 0 };
        using var backend = new SentioraBackend(resolve: FakeSpec, transportFactory: _ => fake);

        var ex = await Assert.ThrowsAsync<SentioraProtocolException>(
            () => backend.OpenAsync("sh", 80, 24, NoSink));

        Assert.Equal(SentioraErrorKind.SessionLimit, ex.Kind);
        Assert.Contains("concurrent-session cap", ex.Message);
        // The raw runtime message is a bare "Session limit", which tells an operator
        // nothing about what to do; it is preserved for diagnosis, not surfaced raw.
        Assert.Equal("Session limit", ex.SentioraMessage);
    }

    [Fact]
    public async Task A_create_failure_does_not_consume_a_slot_on_the_host()
    {
        var fake = new FakeSentioraTransport { FailCreateWith = "Invalid command" };
        using var host = await SentioraHost.StartAsync(fake, FakeSpec());

        await Assert.ThrowsAsync<SentioraProtocolException>(
            () => SentioraConsoleSession.CreateAsync(host, "sh", 80, 24, NoSink));

        // A failed create is not a create/close cycle, so it must not push the host
        // toward its recycling threshold or occupy one of its 16 slots.
        Assert.Equal(0, host.CreateCount);
        Assert.Equal(0, host.LiveSessions);
        Assert.True(host.CanAcceptSession);
    }

    [Fact]
    public async Task An_empty_command_line_is_refused_before_the_runtime_is_contacted()
    {
        var fake = new FakeSentioraTransport();
        using var host = await SentioraHost.StartAsync(fake, FakeSpec());

        // Fail on the caller's input, with a message about it, rather than forwarding
        // an empty command and reporting the runtime's opinion of it.
        await Assert.ThrowsAsync<ArgumentException>(
            () => SentioraConsoleSession.CreateAsync(host, "   ", 80, 24, NoSink));
        Assert.DoesNotContain("terminal.create", fake.Calls);
        Assert.Equal(0, host.CreateCount);
    }

    [Fact]
    public async Task Disposing_the_backend_shuts_every_host_down()
    {
        var harness = new SentioraHarness(maxSessionsPerHost: 1);
        using var first = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        using var second = await harness.Backend.OpenAsync("sh", 80, 24, NoSink);
        Assert.Equal(2, harness.Transports.Count);

        harness.Backend.Dispose();

        Assert.True(harness.Transports.All(t => t.Disposed));
        Assert.Equal(0, harness.Backend.HostCount);
        harness.Dispose();
    }

    [Fact]
    public async Task Opening_on_a_disposed_backend_is_refused()
    {
        var backend = new SentioraBackend(resolve: FakeSpec, transportFactory: _ => new FakeSentioraTransport());
        backend.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => backend.OpenAsync("sh", 80, 24, NoSink));
    }

    [Fact]
    public void The_measured_limits_the_defaults_come_from_are_the_ones_documented()
    {
        // These constants encode measurements from the Sentiora validation suite. If
        // they drift, the adapter recycles at the wrong time — so pin them here where a
        // change has to be deliberate.
        Assert.Equal(16, SentioraHost.MaxConcurrentSessions);
        // Degradation was measured from ~363 and hard failure near ~495; retire early.
        Assert.Equal(360, SentioraHost.RecycleAfterCreates);
        Assert.True(SentioraHost.RecycleAfterCreates < 495);
    }
}
