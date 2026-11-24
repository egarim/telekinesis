using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Telekinesis.Abstractions;
using Telekinesis.Cli;
using Telekinesis.Sentiora;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// The Sentiora console backend against a STUBBED runtime (issue #67): revision and
/// ownership handling, dimension clamping, id mapping, and the synthesised output
/// stream. Deterministic and Node-free by design — see <see cref="FakeSentioraTransport"/>.
/// A green run here proves the adapter speaks the protocol correctly; it is NOT proof
/// the real runtime agrees. <see cref="SentioraRealRuntimeTests"/> is that half.
/// </summary>
public class SentioraAdapterTests
{
    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"condition was still false after {timeoutMs} ms");
    }

    /// <summary>Opens a session and records every chunk pushed to the output callback,
    /// which is the only window onto the synthesised stream.</summary>
    private static async Task<(SentioraConsoleSession Session, List<string> Painted)> OpenAsync(
        SentioraHarness harness, string shell = "cmd.exe", int cols = 80, int rows = 24)
    {
        var painted = new List<string>();
        var session = await harness.Backend.OpenAsync(shell, cols, rows,
            (bytes, count) => { lock (painted) painted.Add(Encoding.UTF8.GetString(bytes, 0, count)); });
        return (session, painted);
    }

    [Fact]
    public async Task Write_presents_a_fresh_revision_and_retries_after_a_stale_observation()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            // A child painting between our observation and our write is the normal case,
            // not a corner: two of the first server-level runs on the Sentiora side hit
            // "Stale observation" before a retry loop existed.
            harness.First.ForcedStaleRejections = 1;
            await session.WriteAsync("dir\r");

            Assert.Equal(2, harness.First.TypeCalls.Count);
            Assert.Equal(0, harness.First.TypeCalls[0].Revision); // the stale one
            Assert.Equal(1, harness.First.TypeCalls[1].Revision); // re-observed, not reused

            // The retry is only possible because the adapter re-observed in between.
            var calls = harness.First.Calls.Where(c => c != "terminal.wait").ToList();
            var firstType = calls.IndexOf("terminal.type");
            Assert.Contains("terminal.observe", calls.Skip(firstType + 1).Take(2));

            // The rejected attempt must not have reached the child.
            Assert.Equal(["dir\r"], harness.First.InputOf());
        }
    }

    [Fact]
    public async Task Write_arms_the_poll_loop_with_the_revision_the_write_returned()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            await session.WriteAsync("dir\r");
            // terminal.type bumps the revision ITSELF. Waiting on the pre-write revision
            // would return immediately, before the child had replied — so the cached
            // value must be the one the write handed back, which is the runtime's truth.
            Assert.Equal(harness.First.RevisionOf(), session.RevisionForTest);
        }
    }

    [Fact]
    public async Task Every_mutating_call_carries_the_owner_token_and_it_is_not_the_session_id()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            await session.WriteAsync("dir\r");
            var owner = harness.First.OwnerOfFirstSession;

            Assert.Equal(owner, harness.First.TypeCalls.Single().Owner);
            // Two distinct bearer values: the id routes, the owner authorises.
            Assert.NotEqual(harness.First.FirstSessionId, owner);
            Assert.Equal(harness.First.FirstSessionId, session.RuntimeSessionId);
        }
    }

    [Fact]
    public async Task An_ownership_failure_is_surfaced_loudly_and_never_retried()
    {
        using var harness = new SentioraHarness();
        harness.First.RejectOwner = true;
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            // The adapter is the only holder of the owner token, so a rejection means a
            // bug here. Retrying a bug is how it stays hidden — fail on the first attempt.
            var ex = await Assert.ThrowsAsync<SentioraProtocolException>(() => session.WriteAsync("dir\r"));
            Assert.Equal(SentioraErrorKind.OwnershipRequired, ex.Kind);
            Assert.Single(harness.First.TypeCalls);
        }
    }

    [Fact]
    public async Task Dimensions_are_clamped_to_the_runtime_range_before_the_call_is_made()
    {
        using var harness = new SentioraHarness();
        // Telekinesis clamps to 2-1000; the runtime refuses anything outside cols 2-300
        // / rows 2-150 with "Invalid dimensions". The fake enforces that too, so this
        // test fails if the adapter ever sends an unclamped request.
        var (session, _) = await OpenAsync(harness, cols: 500, rows: 900);
        using (session)
        {
            var geometry = (IConsoleSessionGeometry)session;
            Assert.Equal(SentioraDimensions.MaxCols, geometry.AppliedCols);
            Assert.Equal(SentioraDimensions.MaxRows, geometry.AppliedRows);
        }
    }

    [Fact]
    public async Task Resize_clamps_and_reports_the_size_actually_applied()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            await session.ResizeAsync(500, 900);
            var geometry = (IConsoleSessionGeometry)session;
            Assert.Equal(300, geometry.AppliedCols);
            Assert.Equal(150, geometry.AppliedRows);
        }
    }

    [Fact]
    public async Task Resize_below_the_minimum_is_clamped_up_not_rejected()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            await session.ResizeAsync(0, -5);
            Assert.Equal(2, ((IConsoleSessionGeometry)session).AppliedCols);
            Assert.Equal(2, ((IConsoleSessionGeometry)session).AppliedRows);
        }
    }

    [Fact]
    public async Task Input_longer_than_the_runtime_limit_is_chunked()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            const int total = SentioraConsoleSession.MaxInputChars + 4356;
            await session.WriteAsync(new string('x', total));

            Assert.Equal(2, harness.First.TypeCalls.Count);
            Assert.Equal(SentioraConsoleSession.MaxInputChars, harness.First.TypeCalls[0].Text.Length);
            Assert.Equal(4356, harness.First.TypeCalls[1].Text.Length);
            Assert.Equal(total, harness.First.InputOf().Sum(s => s.Length));
        }
    }

    [Fact]
    public async Task IsAlive_follows_state_rather_than_the_running_flag()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            Assert.True(session.IsAlive); // "spawned" — a process exists

            harness.First.SetState("operational");
            harness.First.ReleaseWait();
            await Eventually(() => session.StateForTest == "operational");
            Assert.True(session.IsAlive);

            // "exited" and "released" both report running:false, but they are different
            // situations; `state` is what lets the adapter tell them apart.
            harness.First.SetState("exited");
            harness.First.ReleaseWait();
            await Eventually(() => !session.IsAlive);
            Assert.Equal("exited", session.StateForTest);
        }
    }

    [Fact]
    public async Task The_output_callback_receives_a_clear_and_repaint_not_a_raw_byte_stream()
    {
        // THE DOCUMENTED LIMITATION (issue #67): Sentiora never exposes raw child bytes,
        // only the rendered screen. What arrives on the callback is a synthesised
        // clear-and-repaint, so it must not be mistaken for a faithful byte stream.
        using var harness = new SentioraHarness();
        harness.First.InitialScreen = "alpha\nbeta";
        var (session, painted) = await OpenAsync(harness);
        using (session)
        {
            await Eventually(() => painted.Count >= 1);
            string first;
            lock (painted) first = painted[0];

            Assert.StartsWith("\x1b[2J\x1b[H", first);
            Assert.Contains("alpha\r\nbeta", first);
            // There is no escape-sequence or framing fidelity to preserve: the payload is
            // the visible grid and nothing else.
            Assert.DoesNotContain("\x1b[31m", first);
        }
    }

    [Fact]
    public async Task The_synthesised_stream_converges_on_the_runtime_screen_in_TerminalScreen()
    {
        using var harness = new SentioraHarness();
        harness.First.InitialScreen = "alpha\nbeta";
        var screen = new TerminalScreen(80, 24);
        var session = await harness.Backend.OpenAsync("cmd.exe", 80, 24, screen.Feed);
        using (session)
        {
            await Eventually(() => screen.Render().Length > 0);
            Assert.Equal("alpha\nbeta", screen.Render());
        }
    }

    [Fact]
    public async Task An_idle_session_emits_nothing_and_a_new_revision_repaints()
    {
        using var harness = new SentioraHarness();
        harness.First.InitialScreen = "alpha";
        var (session, painted) = await OpenAsync(harness);
        using (session)
        {
            await Eventually(() => painted.Count == 1);

            // A wait that returns the same revision must not repaint: an idle session
            // would otherwise push the whole grid every 250 ms forever.
            harness.First.ReleaseWait();
            await Task.Delay(300);
            Assert.Single(painted);

            harness.First.SetScreen("gamma");
            harness.First.ReleaseWait();
            await Eventually(() => painted.Count == 2);
            lock (painted) Assert.Contains("gamma", painted[1]);
        }
    }

    [Fact]
    public async Task Once_the_backend_is_lost_a_write_fails_immediately_and_is_not_retried()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            harness.First.Break();
            // Let the poll loop notice, so this is deterministic rather than a race
            // between the loop observing the break and the write observing it.
            harness.First.ReleaseWait();
            await Eventually(() => session.BackendLost);

            var ex = await Assert.ThrowsAsync<SentioraProtocolException>(() => session.WriteAsync("dir\r"));
            Assert.Equal(SentioraErrorKind.UnknownSession, ex.Kind);
            Assert.Contains("open a new one", ex.Message);
            // Retrying against a dead runtime would just produce the same failure slower.
            Assert.Empty(harness.First.TypeCalls);
        }
    }

    [Fact]
    public async Task A_transport_break_turns_the_next_request_into_a_transport_failure()
    {
        var fake = new FakeSentioraTransport();
        using var host = await SentioraHost.StartAsync(
            fake, new SentioraLaunchSpec("fake-runtime", [], "test"));

        fake.Break();
        await Assert.ThrowsAsync<SentioraTransportException>(
            () => host.RequestAsync("terminal.observe", new JsonObject { ["sessionId"] = "x" }));
    }

    [Fact]
    public async Task The_owner_token_never_appears_in_an_error_message()
    {
        using var harness = new SentioraHarness();
        harness.First.RejectOwner = true;
        var (session, _) = await OpenAsync(harness);
        using (session)
        {
            var owner = harness.First.OwnerOfFirstSession;
            var ex = await Assert.ThrowsAsync<SentioraProtocolException>(() => session.WriteAsync("dir\r"));

            // The owner token is a bearer credential: it is sent on the wire, and it
            // must never be reported back into a message that could reach a log.
            Assert.DoesNotContain(owner, ex.Message);
            Assert.DoesNotContain(owner, ex.SentioraMessage);
            Assert.DoesNotContain(owner, ex.ToString());
        }
    }

    [Fact]
    public async Task Shell_is_remembered_locally_because_the_runtime_never_echoes_it()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness, shell: "pwsh -NoLogo");
        using (session)
        {
            Assert.Equal("pwsh -NoLogo", session.Shell);
        }
    }

    [Fact]
    public async Task The_command_line_is_split_into_command_and_argv()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness, shell: "pwsh -NoLogo -Command echo");
        using (session)
        {
            // terminal.create takes command + args separately, so the adapter splits.
            var create = Assert.IsType<JsonObject>(harness.First.LastCreateParameters);
            Assert.Equal("pwsh", create["command"]!.GetValue<string>());
            var args = create["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
            Assert.Equal(["-NoLogo", "-Command", "echo"], args);
        }
    }

    [Fact]
    public async Task Dispose_sends_terminal_close_with_the_owner_token()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        var id = session.RuntimeSessionId;
        session.Dispose();

        Assert.Contains("terminal.close", harness.First.Calls);
        Assert.Equal(id, harness.First.LastCloseSessionId);
        Assert.Equal(harness.First.OwnerOfFirstSession, harness.First.LastCloseOwner);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_does_not_close_twice()
    {
        using var harness = new SentioraHarness();
        var (session, _) = await OpenAsync(harness);
        session.Dispose();
        session.Dispose();

        Assert.Equal(1, harness.First.Calls.Count(c => c == "terminal.close"));
    }

    // ---- the Telekinesis-level surface: ids and reported geometry ----

    private static (ConsoleSessionService Service, SentioraHarness Harness) ServiceWithSentiora()
    {
        Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, "1");
        var harness = new SentioraHarness();
        var service = new ConsoleSessionService();
        service.UseSentioraBackendForTest(harness.Backend);
        return (service, harness);
    }

    [Fact]
    public void Telekinesis_ids_stay_con_N_and_the_runtime_UUID_never_surfaces()
    {
        var (service, harness) = ServiceWithSentiora();
        try
        {
            var first = service.Open("cmd.exe", 80, 24);
            var second = service.Open("cmd.exe", 80, 24);

            // Telekinesis ids are con1, con2, …; Sentiora ids are UUIDs. The UUID stays
            // inside the adapter, so the two namespaces never collide or need translating.
            Assert.Equal("con1", first.Id);
            Assert.Equal("con2", second.Id);

            var runtimeId = ((SentioraConsoleSession)first.Session).RuntimeSessionId;
            Assert.True(Guid.TryParse(runtimeId, out _), $"expected a UUID, got '{runtimeId}'");
            Assert.NotEqual(first.Id, runtimeId);
            Assert.DoesNotContain(runtimeId, service.List().Select(e => e.Id));
        }
        finally
        {
            service.Dispose();
            harness.Dispose();
            Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, null);
        }
    }

    [Fact]
    public async Task console_open_reports_the_clamped_dimensions_the_runtime_applied()
    {
        var (service, harness) = ServiceWithSentiora();
        try
        {
            // Telekinesis would happily allocate a 500x900 grid; the runtime cannot.
            // The grid and the reply must both reflect what the PTY actually got, or
            // output mis-wraps and the caller is told a size that does not exist.
            var json = await ConsoleTools.ConsoleOpen(service, "cmd.exe", 500, 900, default);
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(300, doc.RootElement.GetProperty("cols").GetInt32());
            Assert.Equal(150, doc.RootElement.GetProperty("rows").GetInt32());
            Assert.Equal(300, service.Get(doc.RootElement.GetProperty("sessionId").GetString()!).Screen.Cols);
            Assert.Equal(150, service.Get(doc.RootElement.GetProperty("sessionId").GetString()!).Screen.Rows);
        }
        finally
        {
            service.Dispose();
            harness.Dispose();
            Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, null);
        }
    }

    [Fact]
    public async Task console_resize_reports_the_clamped_dimensions_and_resizes_the_grid_to_match()
    {
        var (service, harness) = ServiceWithSentiora();
        try
        {
            var entry = service.Open("cmd.exe", 80, 24);
            var json = await ConsoleTools.ConsoleResize(service, entry.Id, 500, 900, default);
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(300, doc.RootElement.GetProperty("cols").GetInt32());
            Assert.Equal(150, doc.RootElement.GetProperty("rows").GetInt32());
            // The grid must follow, or the PTY and the renderer disagree about wrapping.
            Assert.Equal(300, entry.Screen.Cols);
            Assert.Equal(150, entry.Screen.Rows);
        }
        finally
        {
            service.Dispose();
            harness.Dispose();
            Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, null);
        }
    }

    [Fact]
    public void The_native_backend_is_untouched_when_the_switch_is_off()
    {
        // #67 is explicit that the existing implementation must keep working: with the
        // opt-in switch unset, Windows still refuses exactly as it did before.
        Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, null);
        using var service = new ConsoleSessionService();
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("TELEKINESIS_CONPTY") != "1")
        {
            var ex = Assert.Throws<PlatformNotSupportedException>(() => service.Open("cmd.exe", 80, 24));
            Assert.Contains("#46", ex.Message);
        }
    }

    [Fact]
    public void A_Sentiora_failure_falls_back_to_the_native_backend_and_says_so()
    {
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, "1");
        Environment.SetEnvironmentVariable("TELEKINESIS_CONPTY", null);
        try
        {
            using var service = new ConsoleSessionService();
            // No runtime discoverable here: the fallback must be taken AND announced —
            // #67 requires it to be unambiguous which backend is active, so a silent
            // degrade is the one outcome this path must not have.
            service.UseSentioraBackendForTest(new SentioraBackend(
                resolve: () => throw new SentioraNotFoundException("the runtime is not here")));

            try
            {
                service.Open("cmd.exe", 80, 24);
            }
            catch (PlatformNotSupportedException)
            {
                // Expected on Windows with TELEKINESIS_CONPTY unset: the native gate
                // still applies after the Sentiora backend declines.
            }
            catch (Exception)
            {
                // A non-Windows native backend may reject "cmd.exe"; the point of this
                // test is the announcement, which is asserted below either way.
            }
        }
        finally
        {
            Console.SetError(original);
            Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, null);
        }

        var message = error.ToString();
        Assert.Contains("Sentiora", message);
        Assert.Contains("the runtime is not here", message);
        Assert.Contains("falling back", message);
    }
}

/// <summary>Builds a <see cref="SentioraBackend"/> over fakes, pre-creating the first
/// transport so a test can configure it before any session exists. Later hosts get
/// their own fake, which is how host recycling becomes observable.</summary>
internal sealed class SentioraHarness : IDisposable
{
    private bool _firstUsed;

    public List<FakeSentioraTransport> Transports { get; } = [];

    /// <summary>The "which backend is active" lines, captured instead of printed so a
    /// test run is not drowned in them. <see cref="SentioraBackendTests"/> asserts the
    /// default sink really is stderr.</summary>
    public List<string> Announcements { get; } = [];

    public FakeSentioraTransport First { get; }
    public SentioraBackend Backend { get; }

    public SentioraHarness(int maxSessionsPerHost = 16, int recycleAfterCreates = 360)
    {
        First = new FakeSentioraTransport();
        Transports.Add(First);
        Backend = new SentioraBackend(
            resolve: () => new SentioraLaunchSpec("fake-runtime", [], "test"),
            transportFactory: _ =>
            {
                if (!_firstUsed)
                {
                    _firstUsed = true;
                    return First;
                }
                var transport = new FakeSentioraTransport();
                Transports.Add(transport);
                return transport;
            },
            maxSessionsPerHost: maxSessionsPerHost,
            recycleAfterCreates: recycleAfterCreates,
            announce: message => { lock (Announcements) Announcements.Add(message); });
    }

    public void Dispose()
    {
        Backend.Dispose();
    }
}
