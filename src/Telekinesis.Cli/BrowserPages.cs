using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Shared helpers for driving browsers through the accessibility tree.
/// Browsers expose the whole DOM as a subtree under a Document node — many
/// levels below the window, shadowed by the browser's own chrome. These
/// helpers locate that Document and explain the one failure mode that has no
/// obvious signal: Chromium only realizes the page tree once an AT client
/// queries it (lazy renderer accessibility).
/// </summary>
internal static class BrowserPages
{
    internal const string ActivationHint =
        "The Document exists but has no content. Chromium builds its accessibility tree lazily — "
        + "either relaunch the browser with --force-renderer-accessibility, or interact with the "
        + "page once and retry; a deep query like this one usually warms it within a second.";

    internal const string NoDocumentHint =
        "No Document node found in this application — it is either not a browser, or its renderer "
        + "accessibility is not active yet (Chromium activates it lazily; relaunch with "
        + "--force-renderer-accessibility if retrying does not help).";

    /// <summary>
    /// Locate the application's web page Document. One browser process hosts
    /// every window and tab, each with its own Document (plus devtools and PDF
    /// viewers) — Documents are named by their page title, so
    /// <paramref name="titleContains"/> disambiguates; otherwise the largest
    /// on-screen one wins (background tabs report Offscreen).
    /// </summary>
    internal static async Task<AccessibleElement?> FindDocumentAsync(
        IAccessibilityBackend backend, string applicationId, string? titleContains, CancellationToken ct)
    {
        var docs = await backend.FindElementsAsync(new ElementQuery
        {
            ApplicationId = applicationId,
            Role = AccessibleRole.Document,
            MaxResults = 16,
        }, ct);
        var candidates = string.IsNullOrEmpty(titleContains)
            ? docs
            : docs.Where(d => d.Name?.Contains(titleContains, StringComparison.OrdinalIgnoreCase) == true).ToList();
        var visible = candidates
            .Where(d => d.Bounds is not null && (d.States & ElementState.Offscreen) == 0)
            .ToList();
        return (visible.Count > 0 ? visible : candidates)
                   .Where(d => d.Bounds is not null)
                   .OrderByDescending(d => (long)d.Bounds!.Width * d.Bounds.Height)
                   .FirstOrDefault()
               ?? candidates.FirstOrDefault();
    }

    /// <summary>Roles worth returning to an agent as actionable page elements.</summary>
    internal static bool IsInteractive(AccessibleRole role) => role is
        AccessibleRole.Link or AccessibleRole.Button or AccessibleRole.Edit or
        AccessibleRole.PasswordEdit or AccessibleRole.ComboBox or AccessibleRole.CheckBox or
        AccessibleRole.RadioButton or AccessibleRole.ListItem or AccessibleRole.TabItem or
        AccessibleRole.MenuItem or AccessibleRole.Slider;
}
