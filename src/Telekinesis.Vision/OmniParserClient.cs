using System.Net.Http.Json;
using System.Text.Json;
using Telekinesis.Abstractions;

namespace Telekinesis.Vision;

/// <summary>A UI element detected from pixels. Bounds are screen pixels, ready for click_at.</summary>
/// <param name="Type">Detector class, e.g. "text" or "icon".</param>
/// <param name="Content">OCR text or the model's caption for the element.</param>
/// <param name="Interactive">Whether the model believes the element is clickable.</param>
public sealed record VisionElement(string Type, string? Content, bool Interactive, Bounds Bounds);

/// <summary>
/// Client for a Microsoft OmniParser sidecar — the official `omniparserserver`
/// FastAPI app (POST /parse/ with a base64 image; GET /probe/ health check).
/// OmniParser stays an external service: it needs Python + model weights, so
/// Telekinesis only talks to it and degrades gracefully when it's absent.
/// </summary>
public sealed class OmniParserClient : IDisposable
{
    /// <summary>Endpoint override; the default matches omniparserserver's default port.</summary>
    public const string UrlEnvVar = "TELEKINESIS_OMNIPARSER_URL";

    public static string ConfiguredUrl =>
        Environment.GetEnvironmentVariable(UrlEnvVar) is { Length: > 0 } url ? url : "http://localhost:8000";

    private readonly HttpClient _http;
    public string BaseUrl { get; }

    public OmniParserClient(string? baseUrl = null, HttpClient? http = null)
    {
        BaseUrl = (baseUrl ?? ConfiguredUrl).TrimEnd('/');
        // Parsing a full screenshot can take tens of seconds on CPU-only hosts.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>True when the sidecar answers its health check.</summary>
    public async Task<bool> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync($"{BaseUrl}/probe/", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Parse a screenshot into elements. <paramref name="origin"/> is the screen
    /// position of the image's top-left corner, so returned bounds are absolute
    /// screen pixels even for region captures.
    /// </summary>
    public async Task<IReadOnlyList<VisionElement>> ParseAsync(ScreenImage image, (int X, int Y)? origin = null, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"{BaseUrl}/parse/",
            new { base64_image = Convert.ToBase64String(image.PngData) }, ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("parsed_content_list", out var list) || list.ValueKind != JsonValueKind.Array)
            return [];

        var (ox, oy) = origin ?? (0, 0);
        var results = new List<VisionElement>(list.GetArrayLength());
        foreach (var item in list.EnumerateArray())
        {
            var bounds = ReadBounds(item, image.Width, image.Height, ox, oy);
            if (bounds is null) continue; // no usable box — nothing to click, skip
            results.Add(new VisionElement(
                Type: item.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown",
                Content: item.TryGetProperty("content", out var c) ? NullIfEmpty(c.GetString()) : null,
                Interactive: item.TryGetProperty("interactivity", out var i) && i.ValueKind == JsonValueKind.True,
                Bounds: bounds));
        }
        return results;
    }

    /// <summary>
    /// OmniParser bboxes are [x1,y1,x2,y2] as 0..1 ratios of the image; some
    /// deployments emit absolute pixels instead, so values > 1.5 are treated as
    /// pixels. Degenerate boxes are dropped rather than returned as bad targets.
    /// </summary>
    private static Bounds? ReadBounds(JsonElement item, int width, int height, int ox, int oy)
    {
        if (!item.TryGetProperty("bbox", out var bbox) || bbox.ValueKind != JsonValueKind.Array || bbox.GetArrayLength() < 4)
            return null;
        Span<double> v = stackalloc double[4];
        var n = 0;
        foreach (var e in bbox.EnumerateArray())
        {
            if (n == 4) break;
            if (e.ValueKind != JsonValueKind.Number) return null;
            v[n++] = e.GetDouble();
        }
        var ratios = v[0] <= 1.5 && v[1] <= 1.5 && v[2] <= 1.5 && v[3] <= 1.5;
        var x1 = ratios ? v[0] * width : v[0];
        var y1 = ratios ? v[1] * height : v[1];
        var x2 = ratios ? v[2] * width : v[2];
        var y2 = ratios ? v[3] * height : v[3];
        int x = (int)x1, y = (int)y1, w = (int)(x2 - x1), h = (int)(y2 - y1);
        if (w <= 0 || h <= 0 || w > width || h > height) return null;
        return new Bounds(ox + x, oy + y, w, h);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose() => _http.Dispose();
}
