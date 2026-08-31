using Telekinesis.Abstractions;

namespace Telekinesis.Medium;

/// <summary>
/// Wraps an accessibility backend and merges a <see cref="MediumManifest"/> onto every
/// element it returns. This decorates the <em>resolved</em> backend (so it composes with
/// the browser provider rather than competing with it), and is only constructed when an
/// app actually ships a Medium manifest — so non-Medium apps are untouched.
///
/// Medium enrichment adds advisory metadata (semantic id, intent, risk, confirmation);
/// it never changes which elements exist, their addresses, or their native actions.
/// </summary>
public sealed class MediumEnrichingBackend(IAccessibilityBackend inner, MediumManifest manifest)
    : DelegatingAccessibilityBackend(inner)
{
    private readonly MediumManifest _manifest = manifest;

    public override async Task<AccessibleElement> GetTreeAsync(string applicationId, int maxDepth = 3, CancellationToken ct = default) =>
        MediumMerger.EnrichTree(_manifest, await Inner.GetTreeAsync(applicationId, maxDepth, ct).ConfigureAwait(false));

    public override async Task<AccessibleElement> GetSubtreeAsync(ElementRef element, int maxDepth = 3, CancellationToken ct = default) =>
        MediumMerger.EnrichTree(_manifest, await Inner.GetSubtreeAsync(element, maxDepth, ct).ConfigureAwait(false));

    public override async Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(ElementQuery query, CancellationToken ct = default)
    {
        var found = await Inner.FindElementsAsync(query, ct).ConfigureAwait(false);
        return found.Select(e => MediumMerger.Enrich(_manifest, e)).ToList();
    }

    public override async Task<AccessibleElement> ReadElementAsync(ElementRef element, CancellationToken ct = default) =>
        MediumMerger.Enrich(_manifest, await Inner.ReadElementAsync(element, ct).ConfigureAwait(false));
}
