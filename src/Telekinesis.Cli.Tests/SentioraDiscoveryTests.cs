using Telekinesis.Sentiora;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// Runtime discovery and the opt-in gate (issue #67). Telekinesis must keep working
/// with Sentiora entirely absent, and when the backend IS requested but cannot be
/// found the failure has to name what was tried — a silent fallback would make the two
/// backends incomparable, which is the whole point of the issue.
/// </summary>
public class SentioraDiscoveryTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    private static readonly Func<string, bool> NothingExists = _ => false;

    [Fact]
    public void The_backend_is_off_unless_the_switch_is_exactly_1()
    {
        // Mirrors the existing TELEKINESIS_CONPTY gate: a single unambiguous value, so
        // there is no doubt about which backend a run used.
        Assert.False(SentioraDiscovery.IsEnabled(Env()));
        Assert.False(SentioraDiscovery.IsEnabled(Env((SentioraDiscovery.EnableVariable, "0"))));
        Assert.False(SentioraDiscovery.IsEnabled(Env((SentioraDiscovery.EnableVariable, "true"))));
        Assert.False(SentioraDiscovery.IsEnabled(Env((SentioraDiscovery.EnableVariable, ""))));
        Assert.True(SentioraDiscovery.IsEnabled(Env((SentioraDiscovery.EnableVariable, "1"))));
    }

    [Fact]
    public void An_explicit_command_line_wins_and_is_split_into_argv()
    {
        var spec = SentioraDiscovery.Resolve(
            Env((SentioraDiscovery.CommandVariable, "node /opt/sentiora/src/server.js --flag")),
            NothingExists);

        Assert.Equal("node", spec.FileName);
        Assert.Equal(["/opt/sentiora/src/server.js", "--flag"], spec.Arguments);
        Assert.Equal(SentioraDiscovery.CommandVariable, spec.Source);
    }

    [Fact]
    public void A_quoted_executable_in_the_command_line_survives_as_one_token()
    {
        var spec = SentioraDiscovery.Resolve(
            Env((SentioraDiscovery.CommandVariable, @"""C:\Program Files\nodejs\node.exe"" C:\src\server.js")),
            NothingExists);

        Assert.Equal(@"C:\Program Files\nodejs\node.exe", spec.FileName);
        Assert.Equal([@"C:\src\server.js"], spec.Arguments);
    }

    [Fact]
    public void The_command_line_takes_precedence_over_the_server_path()
    {
        var spec = SentioraDiscovery.Resolve(
            Env(
                (SentioraDiscovery.CommandVariable, "node /from/cmd.js"),
                (SentioraDiscovery.ServerVariable, "/from/server.js")),
            _ => true);

        Assert.Equal(SentioraDiscovery.CommandVariable, spec.Source);
        Assert.Equal(["/from/cmd.js"], spec.Arguments);
    }

    [Fact]
    public void A_server_path_runs_under_node_by_default()
    {
        var spec = SentioraDiscovery.Resolve(
            Env((SentioraDiscovery.ServerVariable, "/opt/sentiora/src/server.js")),
            path => path == "/opt/sentiora/src/server.js");

        Assert.Equal("node", spec.FileName);
        Assert.Equal(["/opt/sentiora/src/server.js"], spec.Arguments);
        Assert.Equal(SentioraDiscovery.ServerVariable, spec.Source);
    }

    [Fact]
    public void An_explicit_node_executable_is_honoured()
    {
        var spec = SentioraDiscovery.Resolve(
            Env(
                (SentioraDiscovery.ServerVariable, "/opt/server.js"),
                (SentioraDiscovery.NodeVariable, "/usr/local/bin/node22")),
            path => path == "/opt/server.js");

        Assert.Equal("/usr/local/bin/node22", spec.FileName);
    }

    [Fact]
    public void A_server_path_that_does_not_exist_says_so_instead_of_spawning_a_mystery_process()
    {
        var ex = Assert.Throws<SentioraNotFoundException>(() => SentioraDiscovery.Resolve(
            Env((SentioraDiscovery.ServerVariable, "/nope/server.js")), NothingExists));

        Assert.Contains("/nope/server.js", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void The_runtime_is_found_on_PATH_when_no_variable_is_set()
    {
        var pathVar = OperatingSystem.IsWindows() ? @"C:\tools\bin;C:\other" : "/usr/bin:/other";
        var expected = Path.Combine(OperatingSystem.IsWindows() ? @"C:\tools\bin" : "/usr/bin",
            SentioraDiscovery.PathCommand);

        var spec = SentioraDiscovery.Resolve(
            Env(("PATH", pathVar)),
            path => path == expected);

        Assert.Equal(expected, spec.FileName);
        Assert.Empty(spec.Arguments);
        Assert.Equal("PATH", spec.Source);
    }

    [Fact]
    public void PATH_entries_are_searched_in_order()
    {
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var second = Path.Combine(OperatingSystem.IsWindows() ? @"C:\second" : "/second",
            SentioraDiscovery.PathCommand);
        var pathVar = string.Join(separator,
            OperatingSystem.IsWindows() ? [@"C:\first", @"C:\second"] : ["/first", "/second"]);

        var spec = SentioraDiscovery.Resolve(Env(("PATH", pathVar)), path => path == second);
        Assert.Equal(second, spec.FileName);
    }

    [Fact]
    public void Nothing_configured_and_nothing_on_PATH_is_a_clear_error_naming_every_option()
    {
        var ex = Assert.Throws<SentioraNotFoundException>(() =>
            SentioraDiscovery.Resolve(Env(("PATH", "")), NothingExists));

        // The operator has to be able to act on this without reading the source.
        Assert.Contains(SentioraDiscovery.CommandVariable, ex.Message);
        Assert.Contains(SentioraDiscovery.ServerVariable, ex.Message);
        Assert.Contains(SentioraDiscovery.PathCommand, ex.Message);
    }

    [Fact]
    public void A_whitespace_only_command_line_falls_through_rather_than_spawning_nothing()
    {
        var ex = Assert.Throws<SentioraNotFoundException>(() => SentioraDiscovery.Resolve(
            Env((SentioraDiscovery.CommandVariable, "   "), ("PATH", "")), NothingExists));
        Assert.Contains(SentioraDiscovery.CommandVariable, ex.Message);
    }

    [Fact]
    public void The_launch_description_quotes_arguments_containing_spaces()
    {
        var spec = new SentioraLaunchSpec("node", [@"C:\Program Files\sentiora\server.js"], "test");
        Assert.Equal(@"node ""C:\Program Files\sentiora\server.js""", spec.Describe());
    }

    [Fact]
    public void The_launch_description_carries_no_secret_material()
    {
        // Discovery is logged verbatim so a run is attributable. A command line is not
        // a secret, but this pins that nothing else — notably no owner token, which is
        // per-session and never known here — can end up in it.
        var spec = SentioraDiscovery.Resolve(
            Env((SentioraDiscovery.CommandVariable, "node /opt/server.js")), NothingExists);
        Assert.Equal("node /opt/server.js", spec.Describe());
        Assert.DoesNotContain("owner", spec.Describe(), StringComparison.OrdinalIgnoreCase);
    }
}
