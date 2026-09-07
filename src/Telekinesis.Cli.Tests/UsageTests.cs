using Telekinesis.Cli;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// The help text is a summary that points at docs/CLI.md, and a summary that omits
/// a command is worse than none — that is how `probe` and `repl` became
/// undiscoverable in the first place (issue #55).
///
/// What these tests actually guarantee, stated honestly:
/// - **Verbs are enforced.** They are read from OneShot.AllVerbs, the same list the
///   dispatcher uses, so adding a verb without documenting it fails here.
/// - **Subcommands are only pinned.** There is no runtime table of them — each is a
///   hand-written `if` in Program.cs — so this catches the help text *dropping* a
///   subcommand, not a *new* one going undocumented. Closing that would mean
///   sharing a dispatch table with Program.cs, which is not worth it for ten `if`s.
/// </summary>
public class UsageTests
{
    /// <summary>The verbs section only, so a match cannot come from prose elsewhere:
    /// bare "read" appears in "--read-only" and would pass vacuously.</summary>
    private static string VerbSection
    {
        get
        {
            var start = Usage.Text.IndexOf("ONE-SHOT VERBS", StringComparison.Ordinal);
            var end = Usage.Text.IndexOf("SUBCOMMANDS", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "help text lost its section headings");
            return Usage.Text[start..end];
        }
    }

    private static string SubcommandSection
    {
        get
        {
            var start = Usage.Text.IndexOf("SUBCOMMANDS", StringComparison.Ordinal);
            var end = Usage.Text.IndexOf("EXIT CODES", StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, "help text lost its section headings");
            return Usage.Text[start..end];
        }
    }

    [Fact]
    public void Every_one_shot_verb_the_dispatcher_knows_is_documented()
    {
        // Enforcing: the list comes from OneShot itself, not a copy of it.
        foreach (var verb in OneShot.AllVerbs)
            Assert.Contains(verb, VerbSection);
    }

    [Theory]
    // Pinned, not enforced — see the class comment. One entry per `if` in Program.cs.
    [InlineData("serve")]
    [InlineData("run")]
    [InlineData("assert")]
    [InlineData("probe")]
    [InlineData("repl")]
    [InlineData("pilot")]
    [InlineData("pilot-eval")]
    [InlineData("doctor")]
    [InlineData("setup")]
    [InlineData("memory")]
    public void Help_still_lists_subcommand(string subcommand)
        => Assert.Contains(subcommand, SubcommandSection);

    [Fact]
    public void Help_states_the_safety_gate_both_ways()
    {
        // The most consequential thing a reader can get wrong: stdio is full-power
        // by default, everything else is opt-in.
        Assert.Contains("--read-only", Usage.Text);
        Assert.Contains("--enable-actions", Usage.Text);
        Assert.Contains("ENABLED", Usage.Text);
    }

    [Fact]
    public void Help_points_at_the_full_reference()
        => Assert.Contains("docs/CLI.md", Usage.Text);

    [Fact]
    public void Version_is_reported_without_build_metadata()
    {
        // Real builds inside a git tree produce "0.9.0+<sha>"; the tail must go.
        var v = Usage.Version;
        Assert.StartsWith("telekinesis ", v);
        Assert.DoesNotContain("+", v);
        Assert.DoesNotContain("unknown", v);
    }
}
