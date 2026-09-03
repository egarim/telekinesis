using Telekinesis.Abstractions;

namespace Telekinesis.Medium;

/// <summary>
/// Merges a <see cref="MediumManifest"/> onto individual normalized accessibility
/// elements. Matching is two-pass: a locale-independent platform automation id first
/// (ordinal, role-plausible — see issue #40), then accessible name (ordinal,
/// case-insensitive), disambiguated by role when several Medium elements share a name.
/// Medium metadata is advisory: only fields it explicitly declares are copied, and the
/// element's own native name/states are never overwritten.
/// </summary>
public static class MediumMerger
{
    /// <summary>Enrich one element with matching Medium metadata. Returns it unchanged when nothing matches.</summary>
    public static AccessibleElement Enrich(MediumManifest? manifest, AccessibleElement element)
    {
        if (manifest is null) return element;
        var match = Match(manifest, element);
        if (match is null) return element;

        // Risk stays Unknown unless explicitly declared — the merge never synthesizes "safe".
        string? risk = match.Risk == MediumRisk.Unknown ? null : match.Risk.ToString().ToLowerInvariant();
        bool? confirm = match.RequiresConfirmation ? true : null;

        return element with
        {
            SemanticId = match.SemanticId,
            Intent = match.Intent,
            Risk = risk,
            RequiresConfirmation = confirm,
            MediumActions = match.Actions.Count > 0 ? match.Actions : null,
            Description = string.IsNullOrWhiteSpace(element.Description) ? match.Description : element.Description,
        };
    }

    /// <summary>Recursively enrich a tree (and its children) — used by get_tree/get_subtree.</summary>
    public static AccessibleElement EnrichTree(MediumManifest? manifest, AccessibleElement element)
    {
        var enriched = Enrich(manifest, element);
        if (enriched.Children is null) return enriched;
        return enriched with { Children = enriched.Children.Select(c => EnrichTree(manifest, c)).ToList() };
    }

    /// <summary>
    /// Find the Medium element that corresponds to <paramref name="element"/>, or null.
    /// A role-plausible automation-id match wins first (explicit automationId, then the
    /// semanticId-as-key convention); otherwise prefer an exact name match, and when
    /// several share a name, disambiguate by role.
    /// </summary>
    public static MediumElement? Match(MediumManifest? manifest, AccessibleElement element)
    {
        if (manifest is null) return null;

        // Locale-independent key first (issue #40): a runtime AutomationId that equals a
        // manifest element's automationId (or, by convention, its semanticId — set your
        // platform automation id / Flutter Semantics identifier to the semantic id) wins
        // outright. Ordinal: platform ids are developer-assigned exact strings.
        if (!string.IsNullOrEmpty(element.AutomationId))
        {
            // Two deterministic tiers: an EXPLICIT manifest automationId always beats the
            // semanticId-as-key convention, and an empty-string automationId (e.g. from a
            // hand-written manifest) counts as absent rather than shadowing the fallback.
            var all = AllElements(manifest).ToList();
            var ids = all.Where(m =>
                    string.Equals(m.AutomationId, element.AutomationId, StringComparison.Ordinal))
                .ToList();
            if (ids.Count == 0)
                ids = all.Where(m => string.IsNullOrEmpty(m.AutomationId) &&
                        string.Equals(m.SemanticId, element.AutomationId, StringComparison.Ordinal))
                    .ToList();

            // Ids are only unique per view, and WPF derives AutomationId from x:Name even
            // on containers — so an id candidate must also be role-plausible, exactly like
            // the name path. An incompatible candidate never binds (a Grid named "save"
            // must not receive the Save button's destructive metadata); named elements
            // fall through to name matching instead.
            var byId = ids.FirstOrDefault(m => MediumRoles.IsCompatible(m.Role, element));
            if (byId is not null) return byId;
        }

        if (string.IsNullOrEmpty(element.Name)) return null;

        var candidates = AllElements(manifest)
            .Where(m => !string.IsNullOrEmpty(m.Name) &&
                        string.Equals(m.Name, element.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var byRole = candidates.FirstOrDefault(m => MediumRoles.IsCompatible(m.Role, element));
        return byRole ?? candidates[0];
    }

    /// <summary>All manifest elements, from named views plus app-global ones.</summary>
    public static IEnumerable<MediumElement> AllElements(MediumManifest manifest) =>
        manifest.Views.Values.SelectMany(v => v.Elements).Concat(manifest.Elements);
}
