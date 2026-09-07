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
        public string Shell => "fake";
        public bool IsAlive => true;
        public void Write(string text) => Writes.Add(text);
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
}
