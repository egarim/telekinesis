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
    public void Help_mentions_both_gate_flags()
    {
        // Pins the VOCABULARY, not the semantics — this would still pass if the
        // sentence stated the gate backwards. A stronger check would have to parse
        // English, so this is a guard against the flags vanishing, nothing more.
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

    // ---- help detection (issue #55, and the regression it caused) ----
    // This logic shipped a silent false green in the project's own CI gate:
    // `assert --name help` printed usage and exited 0 instead of running the
    // assertion. These pin the rule in both directions.

    private static bool IsHelp(params string[] args) =>
        Usage.IsHelpRequest(args, OneShot.CanHandle);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("help")]
    public void A_help_word_first_is_always_help(string word)
        => Assert.True(IsHelp(word));

    [Theory]
    // After a subcommand: the form a human actually types.
    [InlineData("serve", "--help")]
    [InlineData("serve", "help")]
    [InlineData("repl", "-?")]
    [InlineData("doctor", "/?")]
    [InlineData("memory", "-h")]
    // After a flag that takes no value, it is still a help request.
    [InlineData("serve", "--enable-actions", "--help")]
    // After a flag's VALUE, which is not a flag, it is still help.
    [InlineData("serve", "--port", "3001", "--help")]
    public void Help_after_a_subcommand_is_help(params string[] args)
        => Assert.True(IsHelp(args));

    [Theory]
    // THE REGRESSION. --name is a case-insensitive substring query, so these are
    // ordinary CI assertions; hijacking them turned exit 1 into a silent exit 0.
    [InlineData("assert", "--role", "Button", "--name", "help")]
    [InlineData("assert", "--name", "--help")]
    [InlineData("assert", "--name", "-h")]
    [InlineData("assert", "--role", "Button", "--name", "-?")]
    // Same shape elsewhere: typing or searching for the literal string.
    [InlineData("probe", "--type", "--help")]
    [InlineData("probe", "--find", "help")]
    [InlineData("pilot", "--model", "-h")]
    public void A_help_word_in_a_flags_value_position_is_a_value(params string[] args)
        => Assert.False(IsHelp(args));

    [Theory]
    // One-shot verbs own their whole line: launch forwards operands to the child,
    // and no verb's operands may be hijacked.
    [InlineData("launch", "--help")]
    [InlineData("apps", "--help")]
    [InlineData("apps", "help")]
    [InlineData("find", "-?")]
    public void A_one_shot_verb_owns_its_arguments(params string[] args)
        => Assert.False(IsHelp(args));

    [Theory]
    [InlineData("serve")]
    [InlineData("doctor")]
    [InlineData("--read-only")]
    public void Ordinary_invocations_are_not_help(params string[] args)
        => Assert.False(IsHelp(args));

    [Fact]
    public void No_arguments_is_not_help()
        => Assert.False(IsHelp());

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version_is_first_argument_only(string word)
    {
        Assert.True(Usage.IsVersionRequest([word]));
        // Not a version request buried later — it could be a flag's value.
        Assert.False(Usage.IsVersionRequest(["assert", "--name", word]));
    }
}
