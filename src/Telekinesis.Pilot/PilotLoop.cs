using System.Text.Json;
using System.Text.Json.Nodes;
using Telekinesis.Abstractions;

namespace Telekinesis.Pilot;

public sealed record PilotStep(
    int Index, string Screen, IReadOnlyList<Candidate> Candidates,
    string RawDecision, PilotAction? Action, string? ValidationError,
    bool Executed, bool Success, string? Result, string? Observation, int BrainMs, int ActMs);

public sealed record PilotOutcome(bool Success, string Reason, int Steps, IReadOnlyList<PilotStep> Trace, string TraceFile);

/// <summary>
/// The pilot loop (issue #10): inspect state → compact ranked candidates → the
/// local brain chooses one schema-constrained action → validate → execute via
/// native-first backend actions → observe by read-back → trace → repeat until
/// `done`, a step budget, or a stall. Every step is logged as a training trace.
/// </summary>
public static class PilotLoop
{
    private static string SystemPrompt => PilotSystemPrompt.Text;

    public static async Task<PilotOutcome> RunAsync(
        IAccessibilityBackend backend, ILocalBrain brain, string applicationId, string goal,
        int maxSteps = 12, bool dryRun = false, Action<string>? say = null, CancellationToken ct = default)
    {
        say ??= _ => { };
        var steps = new List<PilotStep>();
        var trace = new PilotTraceWriter(goal, brain.Name);
        string? lastFeedback = null;
        string? lastSignature = null;
        var stallCount = 0;
        var recentSignatures = new List<string>();

        for (var i = 1; i <= maxSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (candidates, screen, readouts) = await UiCandidates.BuildAsync(backend, applicationId, goal, ct: ct);
            var ids = candidates.Select(c => c.Id).ToHashSet();
            var user = BuildUserMessage(goal, screen, candidates, readouts, lastFeedback);

            var (raw, brainMs) = await brain.DecideAsync(SystemPrompt, user, ct);
            var action = PilotAction.Parse(raw, out var parseError);
            var invalid = parseError ?? action?.Validate(ids);

            // One corrective retry: feed the validation error back (issue #10's
            // "recovery" shape — the rejection itself becomes a training signal).
            if (invalid is not null)
            {
                trace.Write(i, screen, candidates, raw, action, invalid, executed: false, success: false, null, null, brainMs, 0);
                say($"  ⚠ rejected: {invalid} — retrying");
                var retryUser = user + $"\nYour previous reply was rejected: {invalid}. Reply with one valid JSON action.";
                (raw, brainMs) = await brain.DecideAsync(SystemPrompt, retryUser, ct);
                action = PilotAction.Parse(raw, out parseError);
                invalid = parseError ?? action?.Validate(ids);
                if (invalid is not null)
                {
                    steps.Add(new PilotStep(i, screen, candidates, raw, action, invalid, false, false, null, null, brainMs, 0));
                    trace.Write(i, screen, candidates, raw, action, invalid, false, false, null, null, brainMs, 0);
                    return Finish(false, $"invalid action twice: {invalid}", steps, trace);
                }
            }

            say($"▶ step {i}: {Describe(action!, candidates)}   ({brainMs} ms think)");

            if (action!.Action == "done")
            {
                steps.Add(new PilotStep(i, screen, candidates, raw, action, null, false, true, "done", null, brainMs, 0));
                trace.Write(i, screen, candidates, raw, action, null, false, true, "done", null, brainMs, 0);
                return Finish(true, "brain declared the goal complete", steps, trace);
            }

            // Stall guard: the same action repeated, or an A-B-A-B two-cycle,
            // is a loop rather than progress (the real 4B model produced exactly
            // that alternation before readouts closed the feedback loop).
            var signature = $"{action.Action}|{action.Target}|{action.Text}";
            recentSignatures.Add(signature);
            stallCount = signature == lastSignature ? stallCount + 1 : 0;
            lastSignature = signature;
            var n = recentSignatures.Count;
            var twoCycle = n >= 5
                && recentSignatures[n - 1] == recentSignatures[n - 3]
                && recentSignatures[n - 2] == recentSignatures[n - 4]
                && recentSignatures[n - 3] == recentSignatures[n - 5];
            if (stallCount >= 2 || twoCycle)
                return Finish(false, $"stalled repeating {signature}", steps, trace);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (result, observation) = dryRun
                ? (new ActionResult(true, ActionPath.NativeAction), "(dry-run: not executed)")
                : await ExecuteAsync(backend, action, candidates, ct);
            var actMs = (int)sw.ElapsedMilliseconds;

            var step = new PilotStep(i, screen, candidates, raw, action, null,
                !dryRun, result.Success, result.Success ? result.Path.ToString() : result.Error, observation, brainMs, actMs);
            steps.Add(step);
            trace.Write(i, screen, candidates, raw, action, null, !dryRun, result.Success,
                step.Result, observation, brainMs, actMs);
            say($"    → {(result.Success ? $"ok ({step.Result}, {actMs} ms)" : $"FAILED: {result.Error}")}"
                + (observation is not null ? $"  · sees: {observation}" : ""));

            lastFeedback = result.Success
                ? $"Previous action {Describe(action, candidates)} succeeded. Observation: {observation ?? "none"}."
                : $"Previous action {Describe(action, candidates)} FAILED: {result.Error}. Choose a different approach.";
            if (!result.Success && steps.Count(s => !s.Success) >= 3)
                return Finish(false, "three failed actions", steps, trace);
        }
        return Finish(false, $"step budget ({maxSteps}) exhausted", steps, trace);
    }

    private static async Task<(ActionResult Result, string? Observation)> ExecuteAsync(
        IAccessibilityBackend backend, PilotAction action, IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        var target = candidates.FirstOrDefault(c => c.Id == action.Target);
        switch (action.Action)
        {
            case "click":
            {
                var r = await backend.InvokeAsync(target!.Ref, ct: ct);
                return (r, await ObserveAsync(backend, target, ct));
            }
            case "type":
            {
                var r = await backend.SetTextAsync(target!.Ref, action.Text!, ct);
                return (r, await ObserveAsync(backend, target, ct));
            }
            case "press":
                return (await backend.PressKeysAsync(action.Text!, ct), null);
            case "scroll":
                return (await backend.PressKeysAsync("pagedown", ct), null);
            case "wait":
                await Task.Delay(800, ct);
                return (new ActionResult(true, ActionPath.NativeAction), null);
            default:
                return (ActionResult.Failed(ActionPath.NativeAction, $"unhandled action {action.Action}"), null);
        }
    }

    /// <summary>Read the acted-on element back — verification is part of the loop.</summary>
    private static async Task<string?> ObserveAsync(IAccessibilityBackend backend, Candidate target, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct); // let the UI settle
            var el = await backend.ReadElementAsync(target.Ref, ct);
            var text = el.Text ?? el.Name;
            return text is null ? null : text.Length > 80 ? text[..80] + "…" : text;
        }
        catch (StaleElementException)
        {
            return "(element replaced — screen changed)";
        }
    }

    private static string BuildUserMessage(string goal, string screen, IReadOnlyList<Candidate> candidates,
        IReadOnlyList<string> readouts, string? feedback)
    {
        var payload = new JsonObject
        {
            ["goal"] = goal,
            ["screen"] = screen,
            ["readouts"] = new JsonArray(readouts.Select(r => (JsonNode)JsonValue.Create(r)).ToArray()),
            ["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["role"] = c.Role,
                ["label"] = c.Label,
                ["value"] = c.Value,
            }).ToArray()),
        };
        if (feedback is not null) payload["previous"] = feedback;
        return payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string Describe(PilotAction a, IReadOnlyList<Candidate> candidates)
    {
        var label = candidates.FirstOrDefault(c => c.Id == a.Target)?.Label;
        return a.Action switch
        {
            "click" => $"click {a.Target} \"{label}\"",
            "type" => $"type \"{a.Text}\" into {a.Target} \"{label}\"",
            "press" => $"press {a.Text}",
            _ => a.Action,
        };
    }

    private static PilotOutcome Finish(bool success, string reason, List<PilotStep> steps, PilotTraceWriter trace)
    {
        trace.Finish(success, reason);
        return new PilotOutcome(success, reason, steps.Count, steps, trace.Path);
    }
}
