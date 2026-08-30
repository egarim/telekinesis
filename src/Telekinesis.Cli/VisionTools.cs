using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Telekinesis.Abstractions;

namespace Telekinesis.Cli;

/// <summary>
/// Vision tier — for the moments when the accessibility tree fails (canvas
/// apps). Contributed to the perception set by the built-in vision-fallback
/// provider. Read-only; click_at (the acting half) lives in ActionTools.
/// </summary>
[McpServerToolType]
public static class VisionTools
{
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
        var image = await capture.CaptureScreenAsync(PerceptionTools.ParseRegion(region), ct);
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

        var r = PerceptionTools.ParseRegion(region);
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
        return JsonSerializer.Serialize(new { source, elements }, PerceptionTools.Json);
    }

    [McpServerTool(Name = "recall_targets")]
    [Description("Re-locate this application's remembered vision targets (elements successfully acted on before) on the live screen, without running the parser. Returns them with current pixel bounds ready for click_at. Cheap — try this before parse_screen. Pass show=true to draw them as X-ray boxes so the human sees what memory believes.")]
    public static async Task<string> RecallTargets(
        BackendProvider provider,
        VisionMemoryService memoryService,
        [Description("Application id (pid:N) whose remembered targets to recall.")] string applicationId,
        [Description("Draw the recalled targets on the real screen as labeled boxes for a few seconds.")] bool show,
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

        if (show && targets.Count > 0 && backend is IVisualFeedbackBackend visual)
            await visual.HighlightAsync(
                targets.Select(t => new HighlightRegion(t.Bounds, $"{t.Caption} · {t.Score:0.00}")).ToList(),
                TimeSpan.FromSeconds(5), ct);

        return JsonSerializer.Serialize(new { app = appKey, targets, shown = show }, PerceptionTools.Json);
    }

    /// <summary>App identity (stable process name) and window rectangle for anchor normalization.</summary>
    internal static async Task<(string AppKey, Bounds WindowRect)> ResolveAppAsync(
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
}
