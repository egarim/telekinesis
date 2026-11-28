using Telekinesis.Sentiora;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// The pure translation layer between Telekinesis and the runtime (issue #67): error
/// strings, dimension ranges and the command-line split. Every Sentiora error carries
/// the same JSON-RPC code (-32000), so the message string is the only discriminator —
/// which makes these exact strings a contract worth pinning. They are the literals
/// thrown by Sentiora's src/server.js and src/session.js at commit 8efb6e5.
/// </summary>
public class SentioraErrorsTests
{
    // The kind is internal, so the expectation is its name rather than the enum value —
    // a public xunit method cannot take an internal type as a parameter.
    [Theory]
    [InlineData("Stale observation", nameof(SentioraErrorKind.StaleObservation))]
    [InlineData("Session not running", nameof(SentioraErrorKind.SessionNotRunning))]
    [InlineData("Unknown session", nameof(SentioraErrorKind.UnknownSession))]
    [InlineData("Session limit", nameof(SentioraErrorKind.SessionLimit))]
    [InlineData("Invalid dimensions", nameof(SentioraErrorKind.InvalidDimensions))]
    [InlineData("Resize denied", nameof(SentioraErrorKind.ResizeDenied))]
    [InlineData("Invalid command", nameof(SentioraErrorKind.InvalidArgument))]
    [InlineData("Invalid conptyDll", nameof(SentioraErrorKind.InvalidArgument))]
    [InlineData("Invalid input", nameof(SentioraErrorKind.InvalidArgument))]
    [InlineData("Invalid wait", nameof(SentioraErrorKind.InvalidArgument))]
    [InlineData("Unknown key", nameof(SentioraErrorKind.InvalidArgument))]
    [InlineData("Unknown method", nameof(SentioraErrorKind.InvalidArgument))]
    public void Runtime_messages_map_onto_their_kind(string message, string expected)
        => Assert.Equal(expected, SentioraErrors.Map(message).ToString());

    [Theory]
    // terminal.type throws "Input ownership required"; terminal.close throws the shorter
    // "Ownership required". Same failure, two call sites — both must map, or a close
    // failure would fall through to Other and lose its meaning.
    [InlineData("Input ownership required")]
    [InlineData("Ownership required")]
    public void Both_ownership_spellings_map_to_the_same_kind(string message)
        => Assert.Equal(SentioraErrorKind.OwnershipRequired, SentioraErrors.Map(message));

    [Theory]
    [InlineData("")]
    [InlineData("something node-pty threw")]
    // Matching is exact and case-sensitive, so a runtime that rewords a message degrades
    // to Other rather than being silently mis-classified. That is deliberate: guessing
    // would turn a protocol change into a wrong retry decision.
    [InlineData("stale observation")]
    [InlineData("Stale observation ")]
    public void Anything_unrecognised_is_Other_rather_than_a_guess(string message)
        => Assert.Equal(SentioraErrorKind.Other, SentioraErrors.Map(message));

    [Fact]
    public void A_null_message_is_Other()
        => Assert.Equal(SentioraErrorKind.Other, SentioraErrors.Map(null));

    [Fact]
    public void The_mapped_exception_keeps_the_verbatim_message_and_the_shared_code()
    {
        var ex = SentioraErrors.ToException("Stale observation", -32000);
        Assert.Equal("Stale observation", ex.SentioraMessage);
        Assert.Equal(SentioraErrorKind.StaleObservation, ex.Kind);
        Assert.Equal(-32000, ex.ErrorCode);
    }

    [Fact]
    public void An_ownership_failure_message_says_it_is_a_Telekinesis_bug_not_a_runtime_condition()
    {
        // The adapter is the only holder of the owner token, so this message must not
        // invite an operator to retry or blame the runtime.
        var ex = SentioraErrors.ToException("Input ownership required");
        Assert.Contains("Telekinesis bug", ex.Message);
    }

    [Fact]
    public void Known_failures_get_an_actionable_message_and_only_unknown_ones_echo_the_runtime()
    {
        // A bare "Session limit" tells an operator nothing to do. Recognised failures are
        // rewritten into something actionable; only an unrecognised one falls back to
        // quoting the runtime, which is the correct place to surface an unknown message.
        Assert.DoesNotContain("Session limit", SentioraErrors.ToException("Session limit").Message);
        Assert.Contains("Close a console session", SentioraErrors.ToException("Session limit").Message);

        const string novel = "some failure this adapter has never seen";
        Assert.Contains(novel, SentioraErrors.ToException(novel).Message);
    }

    [Fact]
    public void No_error_message_carries_a_session_identifier_that_was_not_already_public()
    {
        // Part of the #67 secret-logging audit: mapping is a pure function of the runtime's
        // message, so nothing from the request — owner token, session UUID, typed text —
        // can reach a log through it. The token-level check lives in SentioraAdapterTests,
        // where a real session and a real owner token exist.
        foreach (var message in new[]
                 {
                     "Stale observation", "Input ownership required", "Ownership required",
                     "Session not running", "Unknown session", "Session limit",
                     "Invalid dimensions", "Resize denied", "Invalid command", "Unknown key",
                 })
        {
            var ex = SentioraErrors.ToException(message);
            Assert.Equal(message, ex.SentioraMessage);
        }
    }

    [Theory]
    [InlineData(500, 300)]   // Telekinesis allows up to 1000; the runtime does not
    [InlineData(301, 300)]
    [InlineData(300, 300)]
    [InlineData(120, 120)]
    [InlineData(2, 2)]
    [InlineData(1, 2)]
    [InlineData(0, 2)]
    [InlineData(-5, 2)]
    public void Columns_are_clamped_into_the_runtime_range(int requested, int expected)
        => Assert.Equal(expected, SentioraDimensions.ClampCols(requested));

    [Theory]
    [InlineData(900, 150)]
    [InlineData(151, 150)]
    [InlineData(150, 150)]
    [InlineData(30, 30)]
    [InlineData(2, 2)]
    [InlineData(1, 2)]
    [InlineData(-1, 2)]
    public void Rows_are_clamped_into_the_runtime_range(int requested, int expected)
        => Assert.Equal(expected, SentioraDimensions.ClampRows(requested));

    [Fact]
    public void The_runtime_range_is_narrower_than_Telekinesis_own_range()
    {
        // If these ever converge, the clamping layer becomes dead code — worth knowing.
        Assert.True(SentioraDimensions.MaxCols < Abstractions.TerminalScreen.MaxDimension);
        Assert.True(SentioraDimensions.MaxRows < Abstractions.TerminalScreen.MaxDimension);
    }

    [Fact]
    public void A_bare_command_has_no_arguments()
    {
        var (command, args) = SentioraCommandLine.Split("cmd.exe");
        Assert.Equal("cmd.exe", command);
        Assert.Empty(args);
    }

    [Fact]
    public void A_command_line_is_split_into_command_and_argv()
    {
        var (command, args) = SentioraCommandLine.Split("pwsh -NoLogo -Command echo");
        Assert.Equal("pwsh", command);
        Assert.Equal(["-NoLogo", "-Command", "echo"], args);
    }

    [Fact]
    public void A_quoted_path_with_spaces_survives_as_one_argument()
    {
        var (command, args) = SentioraCommandLine.Split(@"""C:\Program Files\PowerShell\7\pwsh.exe"" -NoLogo");
        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", command);
        Assert.Equal(["-NoLogo"], args);
    }

    [Fact]
    public void Repeated_whitespace_collapses()
    {
        var (command, args) = SentioraCommandLine.Split("  sh   -i  ");
        Assert.Equal("sh", command);
        Assert.Equal(["-i"], args);
    }

    [Fact]
    public void An_explicit_empty_argument_is_preserved()
    {
        var (_, args) = SentioraCommandLine.Split("prog \"\" tail");
        Assert.Equal(["", "tail"], args);
    }

    [Fact]
    public void An_empty_command_line_is_refused()
    {
        // terminal.create would reject it as "Invalid command" anyway; failing here
        // gives the caller a message about THEIR input rather than the runtime's.
        Assert.Throws<ArgumentException>(() => SentioraCommandLine.Split(""));
        Assert.Throws<ArgumentException>(() => SentioraCommandLine.Split("   "));
    }
}
