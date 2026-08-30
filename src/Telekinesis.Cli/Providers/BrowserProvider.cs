using Telekinesis.Abstractions;

namespace Telekinesis.Cli.Providers;

/// <summary>
/// Built-in provider for browsers. Claims browser processes and upgrades
/// find_elements' default (window) scope: page content is searched first, then
/// the browser's own chrome, so same-named browser controls (Settings, Back…)
/// never shadow page links. Explicit scoping (Within / ExcludeDocumentContent)
/// is always honored untouched.
/// </summary>
internal sealed class BrowserProvider : IProviderPlugin
{
    private static readonly string[] BrowserProcesses =
        ["msedge", "chrome", "chromium", "firefox", "brave", "vivaldi", "opera", "epiphany"];

    public string Name => "browser";
    public int Priority => 10;

    public bool Handles(ApplicationInfo app)
    {
        var name = app.Name?.ToLowerInvariant() ?? "";
        return BrowserProcesses.Any(name.Contains);
    }

    public IAccessibilityBackend Wrap(IAccessibilityBackend baseBackend, ApplicationInfo app)
        => new BrowserAwareBackend(baseBackend);
}

internal sealed class BrowserAwareBackend(IAccessibilityBackend inner) : DelegatingAccessibilityBackend(inner)
{
    public override async Task<IReadOnlyList<AccessibleElement>> FindElementsAsync(
        ElementQuery query, CancellationToken ct = default)
    {
        // Only upgrade an app-scoped, filtered, unscoped search — anything else
        // (explicit scope, catch-alls, cross-app searches) behaves exactly as base.
        if (query.Within is not null || query.ExcludeDocumentContent || query.ApplicationId is null ||
            (query.Role is null && string.IsNullOrEmpty(query.NameContains)))
            return await base.FindElementsAsync(query, ct);

        try
        {
            var doc = await BrowserPages.FindDocumentAsync(Inner, query.ApplicationId, titleContains: null, ct);
            if (doc is null)
                return await base.FindElementsAsync(query, ct);

            var page = await Inner.FindElementsAsync(query with { Within = doc.Ref }, ct);
            if (page.Count >= query.MaxResults)
                return page;
            var chrome = await Inner.FindElementsAsync(
                query with { ExcludeDocumentContent = true, MaxResults = query.MaxResults - page.Count }, ct);
            return [.. page, .. chrome];
        }
        catch (StaleElementException)
        {
            return await base.FindElementsAsync(query, ct);
        }
    }
}
