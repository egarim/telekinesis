using System.Text.Json;
using Telekinesis.Abstractions;
using Telekinesis.Cli;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// The behaviour half of issue #57: does an omitted `sendEnter` actually submit
/// the command? The schema tests prove the parameter is optional; these prove what
/// the PTY receives, which is what the bug was really about — a command typed at
/// the prompt and never run.
/// </summary>
public class ConsoleWriteTests
{
    private sealed class FakeSession : IConsoleSession
    {
        public readonly List<string> Writes = [];
        /// <summary>Simulates a child that has stopped reading stdin (issue #61).</summary>
        public bool Stuck;
        public string Shell => "fake";
        public bool IsAlive => true;
        public bool Write(string text, TimeSpan timeout)
        {
            if (Stuck) return false;
            Writes.Add(text);
            return true;
        }
        public void Resize(int cols, int rows) { }
        public void Dispose() { }
    }

    private static (ConsoleSessionService svc, FakeSession pty, string id) Session()
    {
        var svc = new ConsoleSessionService();
        var pty = new FakeSession();
        var entry = svc.RegisterForTest(pty, new TerminalScreen(80, 24));
        return (svc, pty, entry.Id);
    }

    [Fact]
    public async Task Omitted_sendEnter_submits_the_command()
    {
        var (svc, pty, id) = Session();
        // Exactly the call an agent makes when it just wants to run something.
        await ConsoleTools.ConsoleWrite(svc, id, "ls -la");
        Assert.Equal(["ls -la\r"], pty.Writes);
    }

    [Fact]
    public async Task Explicit_null_sendEnter_also_submits()
    {
        var (svc, pty, id) = Session();
        await ConsoleTools.ConsoleWrite(svc, id, "ls -la", null);
        Assert.Equal(["ls -la\r"], pty.Writes);
    }

    [Fact]
    public async Task Explicit_false_types_without_submitting()
    {
        var (svc, pty, id) = Session();
        // Answering a single-keypress prompt, or sending a control character.
        await ConsoleTools.ConsoleWrite(svc, id, "", false);
        Assert.Equal([""], pty.Writes);
    }

    [Fact]
    public async Task Explicit_true_submits()
    {
        var (svc, pty, id) = Session();
        await ConsoleTools.ConsoleWrite(svc, id, "echo hi", true);
        Assert.Equal(["echo hi\r"], pty.Writes);
    }

    [Fact]
    public async Task Resize_reports_the_clamped_dimensions_it_applied()
    {
        var (svc, _, id) = Session();
        var json = await ConsoleTools.ConsoleResize(svc, id, 100_000, 100_000, default);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(TerminalScreen.MaxDimension, doc.RootElement.GetProperty("cols").GetInt32());
        Assert.Equal(TerminalScreen.MaxDimension, doc.RootElement.GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task A_child_that_stopped_reading_is_reported_not_silently_hung()
    {
        // Issue #61: an unbounded write used to block the whole MCP request. The
        // bound turns that into an answer the agent can act on.
        var (svc, pty, id) = Session();
        pty.Stuck = true;
        var json = await ConsoleTools.ConsoleWrite(svc, id, "ls");
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("not reading stdin", doc.RootElement.GetProperty("note").GetString());
    }

    [Fact]
    public void Opening_past_the_cap_is_refused_and_says_how_to_recover()
    {
        // Issue #62: each session is a live child process; a runaway agent used to
        // leak them for the life of the server.
        var svc = new ConsoleSessionService();
        for (var i = 0; i < ConsoleSessionService.MaxSessions; i++)
            svc.RegisterForTest(new FakeSession(), new TerminalScreen(80, 24));

        var ex = Assert.Throws<InvalidOperationException>(() => svc.Open("sh", 80, 24));
        Assert.Contains("console_close", ex.Message);
        Assert.Contains("console_list", ex.Message);
    }

    [Fact]
    public void A_megabyte_paste_to_a_child_that_never_reads_stdin_is_BOUNDED()
    {
        // Issue #61's actual contract: a write must never hang the caller. It may
        // succeed or report false — what it may not do is block indefinitely.
        //
        // Asserting a specific outcome would be wrong: whether a stuck child's queue
        // fills or the tty line discipline discards the overflow is platform
        // behaviour (macOS drops past MAX_INPUT; Linux fills and blocks). The bound
        // is the invariant on both. `sleep` never reads stdin.
        if (OperatingSystem.IsWindows()) return; // ConPTY is gated off (#46/#49)

        using var consoles = new ConsoleSessionService();
        var entry = consoles.Open("/bin/sh -c 'exec sleep 30'", 80, 24);

        var big = new string('x', 4 * 1024 * 1024); // far past any pty input queue
        var sw = System.Diagnostics.Stopwatch.StartNew();
        entry.Session.Write(big, TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
            $"write took {sw.Elapsed.TotalSeconds:F1}s - the deadline is not being honoured");
        consoles.Close(entry.Id);
    }

    [Fact]
    public void A_write_to_a_child_that_IS_reading_still_succeeds()
    {
        // The counterpart: bounding must not break the ordinary path.
        if (OperatingSystem.IsWindows()) return;

        using var consoles = new ConsoleSessionService();
        var entry = consoles.Open("/bin/sh", 80, 24);
        Assert.True(entry.Session.Write("echo ok\r", TimeSpan.FromSeconds(5)));
        consoles.Close(entry.Id);
    }
}
