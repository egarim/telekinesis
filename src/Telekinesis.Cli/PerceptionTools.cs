using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Read-only tools — "clairvoyant mode". These work without input-injection
/// permissions and are safe to expose on their own.
/// </summary>
[McpServerToolType]
public static class PerceptionTools
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool(Name = "list_applications")]
    [Description("List all applications visible to the platform accessibility layer.")]
    public static async Task<string> ListApplications(BackendProvider provider, CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var apps = await backend.ListApplicationsAsync(ct);
        return JsonSerializer.Serialize(apps, Json);
    }

    [McpServerTool(Name = "get_tree")]
    [Description("Get the accessibility tree of one application, depth-limited. Use small depths (2-4) and drill down; trees can be huge.")]
    public static async Task<string> GetTree(
        BackendProvider provider,
        [Description("Application id from list_applications.")] string applicationId,
        [Description("Maximum depth to descend (default 3).")] int maxDepth,
        CancellationToken ct)
    {
        var backend = await provider.GetForAppAsync(applicationId, ct);
        var tree = await backend.GetTreeAsync(applicationId, maxDepth <= 0 ? 3 : maxDepth, ct);
        return JsonSerializer.Serialize(tree, Json);
    }

    [McpServerTool(Name = "find_elements")]
    [Description("Search for elements by role and/or name substring, optionally within one application. Prefer this over dumping trees. For browsers, scope 'page' searches only the web page content and 'chrome' only the browser's own UI — without it, page links are shadowed by same-named browser controls.")]
    public static async Task<string> FindElements(
        BackendProvider provider,
        [Description("Normalized role, e.g. Button, Edit, MenuItem. Empty for any.")] string? role,
        [Description("Case-insensitive substring of the element name. Empty for any.")] string? nameContains,
        [Description("Restrict to this application id; empty searches all.")] string? applicationId,
        [Description("For browser windows: 'window' (default, everything), 'page' (web page content only), 'chrome' (browser UI only). Requires applicationId.")] string? scope,
        CancellationToken ct)
    {
        // Routed through the provider registry: for a claimed app (e.g. a browser)
        // the default scope gets the plugin's higher-fidelity search.
        var backend = await provider.GetForAppAsync(applicationId, ct);
        var query = new ElementQuery
        {
            ApplicationId = string.IsNullOrEmpty(applicationId) ? null : applicationId,
            Role = Enum.TryParse<AccessibleRole>(role, ignoreCase: true, out var r) ? r : null,
            NameContains = string.IsNullOrEmpty(nameContains) ? null : nameContains,
        };

        switch (string.IsNullOrEmpty(scope) ? "window" : scope.ToLowerInvariant())
        {
            case "window":
                break;
            case "chrome":
                if (query.ApplicationId is null)
                    throw new ArgumentException("scope 'chrome' requires applicationId.");
                query = query with { ExcludeDocumentContent = true };
                break;
            case "page":
                if (query.ApplicationId is null)
                    throw new ArgumentException("scope 'page' requires applicationId.");
                var doc = await BrowserPages.FindDocumentAsync(backend, query.ApplicationId, titleContains: null, ct)
                    ?? throw new InvalidOperationException(BrowserPages.NoDocumentHint);
                query = query with { Within = doc.Ref };
                break;
            default:
                throw new ArgumentException($"Unknown scope '{scope}' (expected window, page or chrome).");
        }

        var results = await backend.FindElementsAsync(query, ct);
        return JsonSerializer.Serialize(results, Json);
    }

    [McpServerTool(Name = "read_page")]
    [Description("Read the web page in a browser as one compact snapshot: reading text plus interactive elements (links, buttons, fields) with ids ready for invoke/set_text. Finds the page's Document automatically — prefer this over get_tree for browsers, where the page sits many levels deep.")]
    public static async Task<string> ReadPage(
        BackendProvider provider,
        [Description("Browser application id (pid:N); empty uses the focused application.")] string? applicationId,
        [Description("Substring of the page title (tab name) to pick, since one browser process hosts every tab and window. Empty picks the largest visible page.")] string? titleContains,
        [Description("Max interactive elements to return (default 120).")] int maxElements,
        [Description("Max characters of reading text (default 6000).")] int maxTextChars,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        if (string.IsNullOrEmpty(applicationId))
        {
            var focused = await backend.GetFocusedAsync(ct)
                ?? throw new ArgumentException("No applicationId given and nothing has focus.");
            applicationId = focused.Ref.ApplicationId;
        }
        if (maxElements <= 0) maxElements = 120;
        if (maxTextChars <= 0) maxTextChars = 6000;

        var doc = await BrowserPages.FindDocumentAsync(backend, applicationId, titleContains, ct);
        if (doc is null)
            return JsonSerializer.Serialize(new { status = "no-document", applicationId, hint = BrowserPages.NoDocumentHint }, Json);

        // Chromium realizes the page tree lazily; the find above usually warms it,
        // so an empty Document gets one deep retry before we report it as inactive.
        var subtree = await backend.GetSubtreeAsync(doc.Ref, maxDepth: 40, ct);
        if ((subtree.Children?.Count ?? 0) == 0)
        {
            subtree = await backend.GetSubtreeAsync(doc.Ref, maxDepth: 40, ct);
            if ((subtree.Children?.Count ?? 0) == 0)
                return JsonSerializer.Serialize(new { status = "empty-document", applicationId, document = doc.Ref, hint = BrowserPages.ActivationHint }, Json);
        }

        var elements = new List<object>(maxElements);
        var text = new System.Text.StringBuilder();
        var truncated = FlattenPage(subtree, elements, text, maxElements, maxTextChars);

        // Documents with a text pattern report the whole reading text at the root —
        // prefer that (correct document order) over the assembled node names.
        var reading = !string.IsNullOrWhiteSpace(subtree.Text) ? subtree.Text! : text.ToString();
        var textTruncated = reading.Length > maxTextChars;
        if (textTruncated) reading = reading[..maxTextChars];

        return JsonSerializer.Serialize(new
        {
            status = "ok",
            applicationId,
            document = new { id = subtree.Ref.Id, name = subtree.Name },
            text = reading,
            textTruncated,
            elements,
            elementsTruncated = truncated,
        }, Json);
    }

    /// <summary>Depth-first flatten preserving document order. Returns true when the element cap was hit.</summary>
    private static bool FlattenPage(
        AccessibleElement node, List<object> elements, System.Text.StringBuilder text, int maxElements, int maxTextChars)
    {
        var truncated = false;
        foreach (var child in node.Children ?? [])
        {
            if (BrowserPages.IsInteractive(child.Role))
            {
                if (elements.Count >= maxElements) { truncated = true; continue; }
                elements.Add(new
                {
                    id = child.Ref.Id,
                    role = child.Role.ToString(),
                    name = child.Name,
                    bounds = child.Bounds,
                    actions = child.Actions.Count > 0 ? child.Actions : null,
                });
            }
            else if (child.Role is AccessibleRole.Text or AccessibleRole.Label or AccessibleRole.Header
                     && !string.IsNullOrWhiteSpace(child.Name) && text.Length < maxTextChars)
            {
                text.AppendLine(child.Name);
            }
            truncated |= FlattenPage(child, elements, text, maxElements, maxTextChars);
        }
        return truncated;
    }

    [McpServerTool(Name = "read_element")]
    [Description("Read full current detail of one element by id. Fails if the element no longer exists — re-query, do not guess.")]
    public static async Task<string> ReadElement(
        BackendProvider provider,
        [Description("Element id from a previous query.")] string elementId,
        [Description("Owning application id.")] string applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetForAppAsync(applicationId, ct);
        var element = await backend.ReadElementAsync(new ElementRef(elementId, applicationId), ct);
        return JsonSerializer.Serialize(element, Json);
    }

    [McpServerTool(Name = "wait_for")]
    [Description("Wait for an accessibility event (e.g. 'focus-changed', or 'state-changed:<name>') up to a timeout. Use after an action to verify its effect instead of polling. Returns the event, or null on timeout.")]
    public static async Task<string> WaitFor(
        BackendProvider provider,
        [Description("Event kind, e.g. 'focus-changed'. Empty matches any event.")] string kind,
        [Description("Timeout in milliseconds (default 2000).")] int timeoutMs,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var evt = await backend.WaitForEventAsync(kind ?? "", TimeSpan.FromMilliseconds(timeoutMs <= 0 ? 2000 : timeoutMs), ct);
        return evt is null ? "null" : JsonSerializer.Serialize(evt, Json);
    }

    [McpServerTool(Name = "highlight")]
    [Description("Draw a labeled box on the real screen over an element or region — show the human what you're looking at or about to act on. Draws only, never takes focus or input; safe in read-only mode.")]
    public static async Task<string> Highlight(
        BackendProvider provider,
        [Description("Element id to highlight; empty when using region.")] string? elementId,
        [Description("Owning application id (required with elementId).")] string? applicationId,
        [Description("Raw region 'x,y,width,height' in screen pixels, when no element id.")] string? region,
        [Description("Short label to show above the box.")] string? label,
        [Description("Display time in ms (default 1500).")] int durationMs,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        if (backend is not IVisualFeedbackBackend visual)
            throw new NotSupportedException($"{backend.Name} does not support on-screen highlighting yet.");

        Bounds bounds;
        if (!string.IsNullOrEmpty(elementId))
        {
            var element = await backend.ReadElementAsync(
                new ElementRef(elementId, applicationId ?? ""), ct);
            bounds = element.Bounds ?? throw new InvalidOperationException(
                "Element has no on-screen bounds to highlight.");
            label ??= element.Name;
        }
        else
        {
            bounds = ParseRegion(region) ?? throw new ArgumentException(
                "Provide either elementId or region.");
        }

        await visual.HighlightAsync([new HighlightRegion(bounds, label)],
            TimeSpan.FromMilliseconds(durationMs <= 0 ? 1500 : durationMs), ct);
        return JsonSerializer.Serialize(new { highlighted = bounds, label }, Json);
    }

    internal static Bounds? ParseRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region)) return null;
        var parts = region.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y) ||
            !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h))
            throw new ArgumentException($"Malformed region '{region}' (expected 'x,y,width,height').");
        return new Bounds(x, y, w, h);
    }

    [McpServerTool(Name = "get_focused")]
    [Description("Get the currently focused element and its application — cheap orientation call.")]
    public static async Task<string> GetFocused(BackendProvider provider, CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var focused = await backend.GetFocusedAsync(ct);
        return focused is null ? "null" : JsonSerializer.Serialize(focused, Json);
    }
}
