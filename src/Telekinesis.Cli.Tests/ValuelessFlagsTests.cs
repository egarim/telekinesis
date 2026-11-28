using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Telekinesis.Cli;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// `Usage.ValuelessFlags` is hand-maintained, and a hand-maintained list is exactly
/// how the original false-green got in: a flag classified wrongly turns a CI
/// assertion into a silent exit 0. So derive the truth from the code instead of
/// trusting the list — `args.Contains("--x")` is presence-only by construction,
/// which is the definition of a valueless flag.
///
/// If someone adds a presence-only flag and forgets the list, this fails with the
/// flag's name rather than the behaviour silently regressing.
/// </summary>
public class ValuelessFlagsTests
{
    private static string CliSourceDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(here)!)!, "Telekinesis.Cli");

    private static HashSet<string> PresenceOnlyFlagsInSource()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(CliSourceDir(), "*.cs", SearchOption.TopDirectoryOnly))
        {
            // Comments describing the pattern are not uses of it — Usage.cs documents
            // this very rule, and matching its prose made the scan find "--x".
            var code = string.Join('\n', File.ReadAllLines(file)
                .Where(l => !l.TrimStart().StartsWith("//")));
            foreach (Match m in Regex.Matches(code, """args\.Contains\("(--[a-z][a-z-]*)"\)"""))
                found.Add(m.Groups[1].Value);
        }
        return found;
    }

    [Fact]
    public void Every_presence_only_flag_in_the_CLI_is_classified_as_valueless()
    {
        var inSource = PresenceOnlyFlagsInSource();
        Assert.NotEmpty(inSource);   // a broken scan must not pass vacuously

        var missing = inSource.Except(Usage.ValuelessFlags).Order().ToList();
        Assert.True(missing.Count == 0,
            $"read with args.Contains(...) but absent from Usage.ValuelessFlags: {string.Join(", ", missing)}. "
            + "A presence-only flag left off that list makes a help word after it read as a value, "
            + "so `telekinesis <verb> --that-flag --help` prints nothing.");
    }

    [Fact]
    public void The_list_carries_no_unexplained_extras()
    {
        // --sse is the one documented exception: not a real flag, just an ignored
        // token older examples pass (docs/CLI.md). Anything else drifting in means
        // the list is being edited by hand again.
        var extras = Usage.ValuelessFlags.Except(PresenceOnlyFlagsInSource()).Order().ToList();
        Assert.Equal(["--sse"], extras);
    }
}
