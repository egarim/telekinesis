using System.Text.Json.Nodes;
using Telekinesis.Pilot;
using Xunit;

namespace Telekinesis.Cli.Tests;

/// <summary>
/// Integration tests against a REAL server speaking /v1/systemone — run
/// open-jev locally (docs/PILOT.md) and set TELEKINESIS_JEV_URL. Skipped
/// otherwise, so CI and a laptop with nothing running stay green.
///
/// These exist because the fake-handler tests can only prove this code is
/// self-consistent. They cannot prove the request shape is one a real
/// implementation accepts, which is the part most likely to be wrong.
/// </summary>
public class JevLiveTests
{
    // ponytail: no Xunit.SkippableFact dependency for two tests — an unset
    // TELEKINESIS_JEV_URL makes these no-ops rather than a skip. They pass
    // vacuously without a server, which is what the name is for.
    private static string? Url => Environment.GetEnvironmentVariable(JevBrain.UrlEnvVar);

    private static readonly IReadOnlyList<BrainOption> Calculator =
    [
        new("c1", "Button \"Seven\""),
        new("c2", "Button \"Plus\""),
        new("c3", "Button \"Equals\""),
        new("c4", "Text \"Display\" showing 7"),
    ];

    private const string State = """
        goal: compute 7 plus 7
        screen: Calculator
        readouts:
          Display = 7
        candidates (id role "label" [=value]):
          c1 Button "Seven"
          c2 Button "Plus"
          c3 Button "Equals"
          c4 Text "Display" =7
        previous: Previous action click c1 "Seven" succeeded. Observation: Display = 7.
        """;

    [Fact]
    public async Task A_real_server_accepts_the_request_and_answers_a_valid_action()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        using var brain = new JevBrain(Url);
        Assert.True(await brain.ProbeAsync());

        var (json, ms) = await brain.DecideAsync("ignored", State, Calculator);
        var action = PilotAction.Parse(json, out var error);
        Assert.Null(error);
        Assert.Null(action!.Validate(Calculator.Select(c => c.Id).ToHashSet()));

        // The point of a System-1 model: the reply carries a calibrated-ish
        // distribution, not prose. If confidence stops arriving, the trace loses
        // the only signal that distinguishes a sure answer from a coin flip.
        Assert.NotNull(JsonNode.Parse(json)!["confidence"]);
        Assert.True(ms > 0);
    }

    /// <summary>Prints the measured per-step latency of the real brain against a
    /// real server — the number docs/PILOT.md quotes, taken rather than assumed.</summary>
    [Fact]
    public async Task A_real_step_is_timed_so_the_documented_number_stays_honest()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        using var brain = new JevBrain(Url);
        var samples = new List<int>();
        for (var i = 0; i <= 5; i++)
        {
            // Vary the state: repeating one prompt measures a cache, not a step.
            var (_, ms) = await brain.DecideAsync("ignored", State.Replace("= 7", $"= {7 + i}"), Calculator);
            if (i > 0) samples.Add(ms);
        }
        samples.Sort();
        Console.WriteLine($"JevBrain per-step latency: median {samples[samples.Count / 2]} ms "
            + $"(min {samples[0]}, max {samples[^1]}) over {samples.Count} steps at {Url}");
        Assert.All(samples, ms => Assert.True(ms > 0));
    }

    [Fact]
    public async Task A_real_server_grounds_the_target_in_the_candidate_list()
    {
        if (string.IsNullOrWhiteSpace(Url)) return;
        using var brain = new JevBrain(Url);
        var (json, _) = await brain.DecideAsync("ignored", State, Calculator);
        var action = PilotAction.Parse(json, out _)!;
        if (action.Action != "click") return; // grounding is only testable on a click
        Assert.Contains(action.Target, Calculator.Select(c => c.Id));
    }
}
