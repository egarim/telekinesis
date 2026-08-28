using Telekinesis.Abstractions;

namespace Telekinesis.Pilot;

/// <summary>One entry of the compact candidate list handed to the brain.</summary>
public sealed record Candidate(string Id, string Role, string Label, string? Value, ElementRef Ref);

/// <summary>
/// Preprocesses the accessibility tree into a small, ranked candidate list
/// (issue #10: "keeping the model input small and ranked may matter as much as
/// the model choice"). The brain sees short ids (c1, c2, …); the map back to
/// real element refs stays on our side of the fence.
/// </summary>
public static class UiCandidates
{
    private static readonly AccessibleRole[] Interactive =
    [
        AccessibleRole.Button, AccessibleRole.Edit, AccessibleRole.Document, AccessibleRole.ComboBox,
        AccessibleRole.CheckBox, AccessibleRole.RadioButton, AccessibleRole.MenuItem, AccessibleRole.ListItem,
        AccessibleRole.TabItem, AccessibleRole.Link, AccessibleRole.Slider, AccessibleRole.TreeItem,
    ];

    private static readonly string[] DigitWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];

    public static async Task<(IReadOnlyList<Candidate> Candidates, string Screen, IReadOnlyList<string> Readouts)> BuildAsync(
        IAccessibilityBackend backend, string applicationId, string goal, int max = 28, CancellationToken ct = default)
    {
        var tree = await backend.GetTreeAsync(applicationId, 1, ct);
        var screen = tree.Children?.FirstOrDefault()?.Name ?? tree.Name ?? applicationId;

        var elements = await backend.FindElementsAsync(new ElementQuery
        {
            ApplicationId = applicationId,
            MaxResults = 80,
        }, ct);

        // Digits in the goal count as their button-label words ("7" ranks "Seven").
        var goalWords = goal.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(w => w.Length == 1 && char.IsAsciiDigit(w[0])
                ? new[] {DigitWords[w[0] - '0']}
                : [w])
            .ToArray();
        var ranked = elements
            .Where(e => e.Bounds is not null
                        && (e.States & ElementState.Visible) != 0
                        && (e.States & ElementState.Enabled) != 0
                        && Interactive.Contains(e.Role)
                        && !string.IsNullOrEmpty(e.Name))
            .Select(e => (Element: e, Score: Score(e, goalWords)))
            .OrderByDescending(x => x.Score)
            .Take(max)
            .ToList();

        var candidates = ranked
            .Select((x, i) => new Candidate(
                Id: $"c{i + 1}",
                Role: x.Element.Role.ToString().ToLowerInvariant(),
                Label: x.Element.Name!,
                Value: Truncate(x.Element.Text, 60),
                Ref: x.Element.Ref))
            .ToList();

        // Readouts: what the app is DISPLAYING (status text, results, counters).
        // Without them the brain is open-loop — it clicked 7+ five times because
        // it could not see the display change. Read-only context, not targets.
        var readouts = elements
            .Where(e => e.Role is AccessibleRole.Text or AccessibleRole.StatusBar or AccessibleRole.Label
                        && (e.States & ElementState.Visible) != 0
                        && !string.IsNullOrEmpty(e.Name))
            .OrderByDescending(e => (e.Bounds?.Width ?? 0) * (e.Bounds?.Height ?? 0))
            .Take(5)
            .Select(e => Truncate(e.Name, 80)!)
            .ToList();
        return (candidates, screen, readouts);
    }

    private static int Score(AccessibleElement e, string[] goalWords)
    {
        var score = e.Role switch
        {
            AccessibleRole.Edit or AccessibleRole.Document => 3,
            AccessibleRole.Button or AccessibleRole.Link => 2,
            _ => 1,
        };
        var name = e.Name!.ToLowerInvariant();
        score += goalWords.Count(w => w.Length > 2 && name.Contains(w)) * 5;
        return score;
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max] + "…";
}
