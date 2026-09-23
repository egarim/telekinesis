using Telekinesis.Pilot;

namespace Telekinesis.Cli;

/// <summary>
/// Picks the pilot's step-policy model from <c>--brain</c> (issue #65). Both the
/// live loop and the replay harness resolve it the same way, so a brain compared
/// offline is the same brain that would drive the UI.
/// </summary>
public static class BrainFactory
{
    public const string Default = "ollama";
    public static readonly string[] Kinds = [Default, "jev"];

    /// <summary>
    /// Build the brain and say whether it is usable. Returns a null brain with the
    /// reason when the kind is unknown or the brain is not reachable/configured —
    /// the caller prints the hint and exits rather than half-starting a run.
    /// </summary>
    public static async Task<(ILocalBrain? Brain, bool Ready, string Hint)> CreateAsync(
        string? kind, string? url, string? model, CancellationToken ct = default)
    {
        switch ((kind ?? Default).ToLowerInvariant())
        {
            case "ollama":
            {
                var brain = new OllamaBrain(url, model);
                return await brain.ProbeAsync(ct)
                    ? (brain, true, "")
                    : (brain, false, $"No local brain at {brain.Name}. Start Ollama (`ollama serve`), pull the model, "
                        + $"or point {OllamaBrain.UrlEnvVar} at a machine that has one.");
            }
            case "jev":
            {
                var brain = new JevBrain(url, model);
                return await brain.ProbeAsync(ct)
                    ? (brain, true, "")
                    : (brain, false, $"No Jev API key. Set {JevBrain.KeyEnvVar} to a TypeSafe key "
                        + $"(override the endpoint with {JevBrain.UrlEnvVar}).");
            }
            default:
                return (null, false, $"Unknown brain '{kind}'. Use one of: {string.Join(", ", Kinds)}.");
        }
    }
}
