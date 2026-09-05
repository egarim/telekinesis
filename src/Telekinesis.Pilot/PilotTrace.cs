using System.Text.Json;
using System.Text.Json.Nodes;

namespace Telekinesis.Pilot;

/// <summary>
/// Training-trace logger (issue #10): goal, state, ranked candidates, chosen
/// action, executor result, observation, and outcome — one JSONL file per run
/// under the state dir. These traces feed the prompted baseline eval, LoRA
/// fine-tuning, and distillation into a smaller action router.
/// </summary>
public sealed class PilotTraceWriter
{
    private readonly object _gate = new();

    public string Path { get; }

    public PilotTraceWriter(string goal, string brain)
    {
        var dir = System.IO.Path.Combine(StateRoot(), "telekinesis", "pilot-traces");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Slug(goal)}.jsonl");
        WriteLine(new JsonObject
        {
            ["kind"] = "run",
            ["ts"] = DateTimeOffset.Now.ToString("O"),
            ["goal"] = goal,
            ["brain"] = brain,
        });
    }

    public void Write(int step, string screen, IReadOnlyList<Candidate> candidates, string raw,
        PilotAction? action, string? validationError, bool executed, bool success,
        string? result, string? observation, int brainMs, int actMs)
        => WriteLine(new JsonObject
        {
            ["kind"] = "step",
            ["step"] = step,
            ["screen"] = screen,
            ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["role"] = c.Role, ["label"] = c.Label, ["value"] = c.Value,
            }).ToArray()),
            ["raw"] = raw,
            ["action"] = action is null ? null : new JsonObject
            {
                ["action"] = action.Action, ["target"] = action.Target, ["text"] = action.Text,
            },
            ["invalid"] = validationError,
            ["executed"] = executed,
            ["success"] = success,
            ["result"] = result,
            ["observation"] = observation,
            ["brainMs"] = brainMs,
            ["actMs"] = actMs,
        });

    public void Finish(bool success, string reason)
        => WriteLine(new JsonObject
        {
            ["kind"] = "outcome",
            ["success"] = success,
            ["reason"] = reason,
            ["ts"] = DateTimeOffset.Now.ToString("O"),
        });

    private void WriteLine(JsonObject o)
    {
        lock (_gate) File.AppendAllText(Path, o.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + Environment.NewLine);
    }

    private static string StateRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(xdg)) return xdg;
        return OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telekinesis", "state")
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
    }

    private static string Slug(string s) => new([.. s.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : '-').Take(32)]);
}

/// <summary>
/// Replay/eval harness (issue #10 acceptance): re-run recorded traces through a
/// brain WITHOUT touching the UI and score agreement with the recorded actions,
/// plus latency stats. Lets model candidates be compared offline on real data.
/// </summary>
public static class PilotEval
{
    public sealed record EvalResult(int Steps, int Agreed, int Invalid, double AgreementRate, int MedianMs, int P95Ms);

    public static async Task<EvalResult> ReplayAsync(string traceFile, ILocalBrain brain, Action<string>? say = null, CancellationToken ct = default)
    {
        say ??= _ => { };
        string? goal = null;
        var latencies = new List<int>();
        int steps = 0, agreed = 0, invalid = 0;

        foreach (var line in File.ReadLines(traceFile))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var node = JsonNode.Parse(line)!.AsObject();
            switch ((string?)node["kind"])
            {
                case "run":
                    goal = (string?)node["goal"];
                    break;
                case "step" when node["invalid"] is null && node["action"] is JsonObject recorded:
                {
                    steps++;
                    var candidates = node["candidates"]!.AsArray();
                    var ids = candidates.Select(c => (string)c!["id"]!).ToHashSet();
                    var user = new JsonObject
                    {
                        ["goal"] = goal,
                        ["screen"] = node["screen"]?.DeepClone(),
                        ["candidates"] = candidates.DeepClone(),
                    }.ToJsonString();

                    var (raw, ms) = await brain.DecideAsync(SystemPromptOf(), user, ct);
                    latencies.Add(ms);
                    var action = PilotAction.Parse(raw, out var err);
                    if (err is not null || action!.Validate(ids) is not null) { invalid++; say($"  step {steps}: INVALID ({err})"); continue; }
                    var match = action.Action == (string?)recorded["action"]
                        && action.Target == (string?)recorded["target"];
                    if (match) agreed++;
                    say($"  step {steps}: {(match ? "agree" : $"differ (chose {action.Action} {action.Target}, recorded {recorded["action"]} {recorded["target"]})")}  {ms} ms");
                    break;
                }
            }
        }

        latencies.Sort();
        return new EvalResult(steps, agreed, invalid,
            steps == 0 ? 0 : (double)agreed / steps,
            latencies.Count == 0 ? 0 : latencies[latencies.Count / 2],
            latencies.Count == 0 ? 0 : latencies[(int)(latencies.Count * 0.95) is var i && i >= latencies.Count ? latencies.Count - 1 : i]);
    }

    /// <summary>The same system prompt the live loop uses, kept in one place via reflection-free duplication.</summary>
    private static string SystemPromptOf() => PilotSystemPrompt.Text;
}

/// <summary>Shared so the live loop and the replay harness always agree.</summary>
public static class PilotSystemPrompt
{
    public const string Text =
        """
        You drive a desktop application through its accessibility tree. Each turn you
        receive the goal, the current screen name, "readouts" (what the app displays),
        and "candidates" — one per line as: <id> <role> "<label>" [=value]. Reply with
        EXACTLY ONE JSON action:
        {"action":"click|type|press|scroll|wait|done","target":"<candidate id>","text":"<text>"}
        Rules:
        - click: activate a button/item; target must be one of the candidate ids.
        - type: replace the text of an editable target (candidate id) with text.
        - press: send a key combination to the app, e.g. "enter", "ctrl+s". No target.
        - scroll: scroll the view. wait: pause briefly when the UI is still changing.
        - done: the goal is COMPLETE and verified by what you can see. Never emit done
          before the goal's effect is visible in the readouts or screen state.
        - "readouts" show what the app currently displays (result fields, status).
          Track your progress there: after each action check whether the readouts
          moved toward the goal, and emit done the moment they show it.
        - Use only candidate ids that exist. One action per reply. No prose.
        """;
}
