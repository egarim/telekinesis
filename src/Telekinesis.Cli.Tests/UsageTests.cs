using Telekinesis.Cli;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// The help text is a summary that points at docs/CLI.md, but a summary that omits
/// a subcommand is worse than none — that is exactly how `probe` and `repl` became
/// undiscoverable in the first place. These pin the inventory so adding a
/// subcommand without listing it fails here (issue #55).
/// </summary>
public class UsageTests
{
    [Theory]
    // Every subcommand dispatched in Program.cs.
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
    public void Help_lists_every_subcommand(string subcommand)
        => Assert.Contains(subcommand, Usage.Text);

    [Theory]
    // Every one-shot verb in OneShot's Perception/Actions arrays.
    [InlineData("apps")]
    [InlineData("tree")]
    [InlineData("find")]
    [InlineData("read")]
    [InlineData("focused")]
    [InlineData("snapshot")]
    [InlineData("click")]
    [InlineData("click-at")]
    [InlineData("invoke")]
    [InlineData("set-text")]
    [InlineData("type")]
    [InlineData("press")]
    [InlineData("launch")]
    public void Help_lists_every_one_shot_verb(string verb)
        => Assert.Contains(verb, Usage.Text);

    [Fact]
    public void Help_states_the_safety_gate_both_ways()
    {
        // The single most consequential thing a reader can get wrong: stdio is
        // full-power by default, everything else is opt-in.
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
        var v = Usage.Version;
        Assert.StartsWith("telekinesis ", v);
        Assert.DoesNotContain("+", v);      // no `+commithash` tail
        Assert.DoesNotContain("unknown", v);
    }
}
