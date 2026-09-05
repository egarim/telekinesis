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

    /// <summary>
    /// Compact text encoding of the turn (issue #10 / token optimization): the
    /// per-step cost is CPU prefill of this message, which scales with its token
    /// count. A verbose JSON array of candidates cost ~1000 tokens/step; this
    /// line format ("c1 button "Seven"", value only when present) is ~5-6 tokens
    /// per candidate — the same information at roughly a fifth the prefill. The
    /// model still replies with the JSON action schema; only the INPUT shrinks.
    /// </summary>
    private static string BuildUserMessage(string goal, string screen, IReadOnlyList<Candidate> candidates,
        IReadOnlyList<string> readouts, string? feedback)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("goal: ").AppendLine(goal);
        sb.Append("screen: ").AppendLine(screen);
        if (readouts.Count > 0)
        {
            sb.AppendLine("readouts:");
            foreach (var r in readouts) sb.Append("  ").AppendLine(Clean(r));
        }
        sb.AppendLine("candidates (id role \"label\" [=value]):");
        foreach (var c in candidates)
            sb.Append("  ").Append(c.Id).Append(' ').Append(c.Role)
              .Append(" \"").Append(Clean(c.Label)).Append('"')
              .AppendLine(string.IsNullOrEmpty(c.Value) ? "" : $" ={Clean(c.Value)}");
        if (feedback is not null) sb.Append("previous: ").AppendLine(Clean(feedback));
        return sb.ToString();
    }

    // Keep the terse line format unambiguous: embedded quotes are escaped and
    // newlines/tabs collapsed to spaces so a control label can't break a record
    // boundary or the quoted-label field (issue #10 / PR #47 review).
    private static string Clean(string s) => s
        .Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

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
