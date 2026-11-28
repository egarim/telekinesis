using System.Diagnostics;
using System.Text.Json;
using Telekinesis.Abstractions;
using Telekinesis.Cli;
using Telekinesis.Sentiora;
using Xunit;
using Xunit.Abstractions;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// THE REAL-RUNTIME HALF of the Sentiora backend tests (issue #67). Everything else in
/// this suite drives <see cref="FakeSentioraTransport"/>, which proves the adapter speaks
/// the protocol correctly but proves NOTHING about the actual runtime. These tests are
/// the only ones that spawn a real Sentiora host process.
///
/// <para><b>They are conditional.</b> A Sentiora runtime has to be discoverable, which
/// means setting one of:</para>
/// <code>
///   TELEKINESIS_SENTIORA_CMD="node /path/to/sentiora-terminal/src/server.js"
///   TELEKINESIS_SENTIORA_SERVER=/path/to/sentiora-terminal/src/server.js
///   # or `sentiora-terminal` on PATH
/// </code>
/// <para>When none is set these tests report the reason and return without asserting, so
/// <b>a green CI run does NOT mean the real integration was verified.</b> xunit 2.x has
/// no dynamic skip, hence the early return; the reason is written to the test output so
/// it is visible in the log rather than invisible behind a pass. They are excluded from
/// the "verified" claim in the PR unless someone ran them with a runtime present.</para>
/// </summary>
public class SentioraRealRuntimeTests(ITestOutputHelper output)
{
    /// <summary>Returns null and explains why when no real runtime is reachable.</summary>
    private SentioraBackend? TryBackend()
    {
        try
        {
            var spec = SentioraDiscovery.Resolve();
            output.WriteLine($"[real-runtime] using {spec.Describe()} (from {spec.Source})");
            return new SentioraBackend(() => spec);
        }
        catch (SentioraNotFoundException ex)
        {
            output.WriteLine($"[real-runtime] SKIPPED — no runtime discoverable: {ex.Message}");
            return null;
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return false;
    }

    [Fact(Timeout = 180_000)]
    public async Task Real_runtime_handshakes_and_reports_its_platform()
    {
        using var backend = TryBackend();
        if (backend is null) return;

        using var screen = new ScreenSink();
        using var session = await backend.OpenAsync(DefaultShell(), 100, 24, screen.Feed);

        Assert.True(session.IsAlive);
        Assert.Equal(DefaultShell(), session.Shell);
        Assert.Equal(100, ((IConsoleSessionGeometry)session).AppliedCols);
        output.WriteLine($"[real-runtime] session alive at {session.AppliedCols}x{session.AppliedRows}");
    }

    /// <summary>
    /// The direct test of telekinesis#49's symptoms. #49 reports that on Windows 11
    /// ARM64 build 26200 the child never binds to the pseudoconsole, produces zero bytes
    /// into the pipe, and renders to the PARENT console instead. All three are checked
    /// here at once: if the child were unbound the screen would stay empty, and if it
    /// rendered to the parent console the host's stdout would carry non-JSON-RPC bytes,
    /// which the transport treats as fatal — so a leak fails this test rather than
    /// passing quietly.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Real_runtime_child_output_reaches_the_screen_and_nothing_leaks_to_the_host_stdio()
    {
        using var backend = TryBackend();
        if (backend is null) return;

        var marker = "TK49_" + Guid.NewGuid().ToString("N")[..12];
        var screen = new TerminalScreen(100, 24);
        var session = await backend.OpenAsync(DefaultShell(), 100, 24, screen.Feed);
        try
        {
            session.Write($"echo {marker}\r", TimeSpan.FromSeconds(30));

            var appeared = await Eventually(() => screen.Render().Contains(marker), 60_000);
            output.WriteLine($"[real-runtime] marker '{marker}' reached the screen: {appeared}");
            output.WriteLine($"[real-runtime] screen:\n{screen.Render()}");

            Assert.True(appeared,
                "The child produced no output through the PTY — this is telekinesis#49's "
                + "zero-bytes symptom. The screen stayed empty.");
        }
        finally
        {
            session.Dispose();
        }

        // Reaching here at all means every line the host wrote was conforming JSON-RPC:
        // a single stray byte from the child would have broken the transport and thrown.
        Assert.False(session.BackendLost,
            "The transport broke — output leaked onto the host's stdio (telekinesis#49's "
            + "renders-to-the-parent-console symptom).");
    }

    [Fact(Timeout = 180_000)]
    public async Task Real_runtime_clamps_an_oversized_resize_instead_of_rejecting_it()
    {
        using var backend = TryBackend();
        if (backend is null) return;

        var screen = new TerminalScreen(100, 24);
        var session = await backend.OpenAsync(DefaultShell(), 100, 24, screen.Feed);
        try
        {
            // Telekinesis would allow 500 columns; the runtime allows 300. Sending 500
            // unclamped comes back as "Invalid dimensions", so this is the real check
            // that the adapter clamps before the call rather than after the failure.
            session.Resize(500, 900);
            var geometry = (IConsoleSessionGeometry)session;
            Assert.Equal(SentioraDimensions.MaxCols, geometry.AppliedCols);
            Assert.Equal(SentioraDimensions.MaxRows, geometry.AppliedRows);

            var settled = await Eventually(
                () => session.AppliedCols == SentioraDimensions.MaxCols, 15_000);
            Assert.True(settled, "the runtime never confirmed the clamped geometry");
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// A stateful interactive session: a variable set by one write must be visible to
    /// the next. This is what distinguishes a real PTY from shelling out per command,
    /// and it is the behaviour #49 says is impossible on this platform.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Real_runtime_session_is_stateful_across_writes()
    {
        using var backend = TryBackend();
        if (backend is null) return;

        var key = "TKSTATE" + Random.Shared.Next(1000, 9999);
        var value = "stateful_" + Guid.NewGuid().ToString("N")[..8];
        var screen = new TerminalScreen(100, 30);
        var session = await backend.OpenAsync(DefaultShell(), 100, 30, screen.Feed);
        try
        {
            var setCommand = OperatingSystem.IsWindows() ? $"set {key}={value}" : $"{key}={value}; export {key}";
            session.Write(setCommand + "\r", TimeSpan.FromSeconds(30));
            await Eventually(() => screen.Render().Contains(value) || screen.Render().Contains(key), 30_000);

            session.Write(OperatingSystem.IsWindows() ? $"echo SEEN=%{key}%\r" : $"echo SEEN=${key}\r", TimeSpan.FromSeconds(30));
            var saw = await Eventually(() => screen.Render().Contains($"SEEN={value}"), 30_000);
            output.WriteLine($"[real-runtime] state persisted across writes: {saw}");
            output.WriteLine($"[real-runtime] screen:\n{screen.Render()}");

            Assert.True(saw,
                "The child did not remember state set by an earlier write, so the session "
                + "is not a persistent PTY.");
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// End to end through the surface an agent actually uses: console_open, write, read,
    /// resize, list, close. This is the test that makes the two backends comparable in
    /// the sense #67 asks for — same tools, same replies, different backend.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task Real_runtime_drives_the_whole_console_tool_surface()
    {
        if (TryBackend() is not { } probe) return;
        probe.Dispose();

        Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, "1");
        try
        {
            using var consoles = new ConsoleSessionService();
            var marker = "TKE2E_" + Guid.NewGuid().ToString("N")[..10];

            // null shell = the OS default, and 500x900 is deliberately outside the
            // runtime's range so the reported size proves the clamp reached the reply.
            var opened = await ConsoleTools.ConsoleOpen(consoles, null, 500, 900, default);
            using var openDoc = JsonDocument.Parse(opened);
            var id = openDoc.RootElement.GetProperty("sessionId").GetString()!;

            // Telekinesis ids are con1, con2, … — the runtime's UUID stays inside the adapter.
            Assert.StartsWith("con", id);
            Assert.Equal(300, openDoc.RootElement.GetProperty("cols").GetInt32());
            Assert.Equal(150, openDoc.RootElement.GetProperty("rows").GetInt32());
            output.WriteLine($"[real-runtime] console_open -> {opened[..Math.Min(200, opened.Length)]}");

            await ConsoleTools.ConsoleWrite(consoles, id, $"echo {marker}", true, default);

            var saw = await EventuallyAsync(async () =>
                (await ConsoleTools.ConsoleRead(consoles, id, 0, default)).Contains(marker), 60_000);
            output.WriteLine($"[real-runtime] console_read saw the marker: {saw}");
            Assert.True(saw, "console_read never showed the command's output");

            var read = await ConsoleTools.ConsoleRead(consoles, id, 0, default);
            Assert.Contains("\"alive\":true", read.Replace(" ", ""));

            var resized = await ConsoleTools.ConsoleResize(consoles, id, 500, 900, default);
            using var resizeDoc = JsonDocument.Parse(resized);
            Assert.Equal(300, resizeDoc.RootElement.GetProperty("cols").GetInt32());
            Assert.Equal(150, resizeDoc.RootElement.GetProperty("rows").GetInt32());

            var listed = await ConsoleTools.ConsoleList(consoles, default);
            Assert.Contains(id, listed);

            var closed = await ConsoleTools.ConsoleClose(consoles, id, default);
            Assert.Contains("\"ok\":true", closed.Replace(" ", ""));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => ConsoleTools.ConsoleRead(consoles, id, 0, default));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SentioraDiscovery.EnableVariable, null);
        }
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition()) return true;
            await Task.Delay(150);
        }
        return false;
    }

    private static string DefaultShell() =>
        OperatingSystem.IsWindows() ? "cmd.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";

    /// <summary>Collects painted chunks so a test can see the synthesised stream itself.</summary>
    private sealed class ScreenSink : IDisposable
    {
        public readonly List<string> Chunks = [];
        public void Feed(byte[] data, int count)
        {
            lock (Chunks) Chunks.Add(System.Text.Encoding.UTF8.GetString(data, 0, count));
        }

        public void Dispose() { }
    }
}
