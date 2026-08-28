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
        var backend = await provider.GetConnectedAsync(ct);
        var tree = await backend.GetTreeAsync(applicationId, maxDepth <= 0 ? 3 : maxDepth, ct);
        return JsonSerializer.Serialize(tree, Json);
    }

    [McpServerTool(Name = "find_elements")]
    [Description("Search for elements by role and/or name substring, optionally within one application. Prefer this over dumping trees.")]
    public static async Task<string> FindElements(
        BackendProvider provider,
        [Description("Normalized role, e.g. Button, Edit, MenuItem. Empty for any.")] string? role,
        [Description("Case-insensitive substring of the element name. Empty for any.")] string? nameContains,
        [Description("Restrict to this application id; empty searches all.")] string? applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        var query = new ElementQuery
        {
            ApplicationId = string.IsNullOrEmpty(applicationId) ? null : applicationId,
            Role = Enum.TryParse<AccessibleRole>(role, ignoreCase: true, out var r) ? r : null,
            NameContains = string.IsNullOrEmpty(nameContains) ? null : nameContains,
        };
        var results = await backend.FindElementsAsync(query, ct);
        return JsonSerializer.Serialize(results, Json);
    }

    [McpServerTool(Name = "read_element")]
    [Description("Read full current detail of one element by id. Fails if the element no longer exists — re-query, do not guess.")]
    public static async Task<string> ReadElement(
        BackendProvider provider,
        [Description("Element id from a previous query.")] string elementId,
        [Description("Owning application id.")] string applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
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

    // ---- Vision tier: for the moments when the accessibility tree fails ----

    [McpServerTool(Name = "screenshot")]
    [Description("Capture the screen (or a region) as PNG — the vision fallback for apps whose accessibility tree is empty or wrong. Prefer the semantic tools; pixels are expensive.")]
    public static async Task<ModelContextProtocol.Protocol.ImageContentBlock> Screenshot(
        BackendProvider provider,
        [Description("Optional region 'x,y,width,height' in screen pixels; empty captures the whole virtual desktop.")] string? region,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        if (backend is not IScreenCaptureBackend capture)
            throw new NotSupportedException($"{backend.Name} does not support screen capture yet.");
        var image = await capture.CaptureScreenAsync(ParseRegion(region), ct);
        return new ModelContextProtocol.Protocol.ImageContentBlock
        {
            Data = image.PngData,
            MimeType = "image/png",
        };
    }

    [McpServerTool(Name = "parse_screen")]
    [Description("Screenshot the screen (or a region) and parse it into UI elements with an OmniParser sidecar. Last resort when the accessibility tree fails; returned bounds are screen pixels usable with click_at. A screen seen before is answered instantly from perceptual memory (source:'memory'); pass applicationId to enable memory and anchor learning. Requires an OmniParser server (see docs/VISION.md) for fresh parses.")]
    public static async Task<string> ParseScreen(
        BackendProvider provider,
        VisionMemoryService memoryService,
        [Description("Optional region 'x,y,width,height' in screen pixels; empty parses the whole virtual desktop.")] string? region,
        [Description("Owning application id (pid:N) — enables the parse cache and anchor learning for this app.")] string? applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        if (backend is not IScreenCaptureBackend capture)
            throw new NotSupportedException($"{backend.Name} does not support screen capture yet.");

        var r = ParseRegion(region);
        var image = await capture.CaptureScreenAsync(r, ct);
        var origin = r is null ? (0, 0) : (r.X, r.Y);

        // App identity for memory: the process name outlives the pid across runs.
        var (appKey, windowRect) = await ResolveAppAsync(backend, applicationId, r, image, ct);

        var memory = string.IsNullOrEmpty(applicationId) ? null : memoryService.Memory;
        IReadOnlyList<Telekinesis.Vision.VisionElement>? elements = memory?.TryRecallParse(appKey, image);
        var source = elements is not null ? "memory" : "omniparser";
        if (elements is null)
        {
            using var parser = new Telekinesis.Vision.OmniParserClient();
            if (!await parser.ProbeAsync(ct))
                throw new InvalidOperationException(
                    $"No OmniParser server at {parser.BaseUrl}. Start one (see docs/VISION.md) or set {Telekinesis.Vision.OmniParserClient.UrlEnvVar}.");
            elements = await parser.ParseAsync(image, r is null ? null : (r.X, r.Y), ct);
            memory?.StoreParse(appKey, image, elements);
        }

        memoryService.Last = new VisionMemoryService.LastParse(appKey, windowRect, image, origin, elements);
        return JsonSerializer.Serialize(new { source, elements }, Json);
    }

    [McpServerTool(Name = "recall_targets")]
    [Description("Re-locate this application's remembered vision targets (elements successfully acted on before) on the live screen, without running the parser. Returns them with current pixel bounds ready for click_at. Cheap — try this before parse_screen.")]
    public static async Task<string> RecallTargets(
        BackendProvider provider,
        VisionMemoryService memoryService,
        [Description("Application id (pid:N) whose remembered targets to recall.")] string applicationId,
        CancellationToken ct)
    {
        var backend = await provider.GetConnectedAsync(ct);
        if (backend is not IScreenCaptureBackend capture)
            throw new NotSupportedException($"{backend.Name} does not support screen capture yet.");
        var memory = memoryService.Memory
            ?? throw new NotSupportedException("Perceptual memory is not available on this platform yet.");

        var image = await capture.CaptureScreenAsync(null, ct);
        var (appKey, windowRect) = await ResolveAppAsync(backend, applicationId, null, image, ct);
        var targets = memory.Recall(appKey, windowRect, image, (0, 0));
        return JsonSerializer.Serialize(new { app = appKey, targets }, Json);
    }

    /// <summary>App identity (stable process name) and window rectangle for anchor normalization.</summary>
    private static async Task<(string AppKey, Bounds WindowRect)> ResolveAppAsync(
        IAccessibilityBackend backend, string? applicationId, Bounds? region, ScreenImage image, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(applicationId))
        {
            var tree = await backend.GetTreeAsync(applicationId, 1, ct);
            var window = tree.Children?.FirstOrDefault(w => w.Bounds is not null);
            if (window?.Bounds is { } wb)
                return (tree.Name ?? applicationId, wb);
            return (tree.Name ?? applicationId, region ?? new Bounds(0, 0, image.Width, image.Height));
        }
        return ("screen", region ?? new Bounds(0, 0, image.Width, image.Height));
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
