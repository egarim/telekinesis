namespace Telekinesis.Abstractions;

/// <summary>A screen region to highlight, with an optional short label.</summary>
public sealed record HighlightRegion(Bounds Bounds, string? Label = null);

/// <summary>
/// Optional backend capability: draw transient highlights on the real screen —
/// the X-ray overlay. Pure output: implementations must never take focus,
/// intercept input, or appear in the accessibility tree they visualize.
/// Same opt-in pattern as the vision tier; consumers type-test and degrade.
/// </summary>
public interface IVisualFeedbackBackend
{
    /// <summary>
    /// Show the given regions for <paramref name="duration"/>, then auto-clear.
    /// A new call replaces the previous set. Zero or negative duration means
    /// "until <see cref="ClearHighlightsAsync"/> or the next call".
    /// </summary>
    Task HighlightAsync(IReadOnlyList<HighlightRegion> regions, TimeSpan duration, CancellationToken ct = default);

    Task ClearHighlightsAsync(CancellationToken ct = default);
}
