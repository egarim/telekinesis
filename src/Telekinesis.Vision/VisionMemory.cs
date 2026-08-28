using System.Text.Json;
using Telekinesis.Abstractions;

namespace Telekinesis.Vision;

/// <summary>A remembered, previously-acted-on vision target.</summary>
/// <param name="NormCx">Element center, normalized to the app window rectangle (0..1).</param>
/// <param name="CropFile">Grayscale-matchable PNG crop of the element, relative to the store dir.</param>
public sealed record Anchor(
    string Id, string AppKey, string? Caption, string Type,
    double NormCx, double NormCy, double NormW, double NormH,
    string CropFile, int Hits, int Misses, DateTimeOffset LastSeen);

/// <summary>An anchor re-located on the live screen, ready for click_at.</summary>
public sealed record RecalledTarget(string Id, string? Caption, string Type, Bounds Bounds, double Score);

internal sealed record ParseCacheEntry(string AppKey, int Width, int Height, ulong Hash, string ElementsJson, DateTimeOffset At);

/// <summary>
/// Perceptual memory: Telekinesis learns from previous vision runs.
/// - Parse cache: a screen already parsed (same perceptual hash) returns its
///   elements instantly instead of re-running the OmniParser sidecar.
/// - Anchors: targets the agent successfully acted on are remembered as window-
///   relative positions plus a pixel crop, re-located later by template match.
/// - Feedback: anchors that stop matching decay and are evicted.
/// The store doubles as a training dataset (see <see cref="Export"/>): grounded
/// crops with captions and verified-use counts, ready for adapter fine-tuning.
/// </summary>
public sealed class VisionMemory
{
    private const int RecallRadius = 96;         // px search window around the expected spot
    private const int EvictAfterMisses = 3;

    private readonly IRasterCodec _codec;
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly List<ParseCacheEntry> _parseCache;
    private readonly List<Anchor> _anchors;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static string DefaultDir =>
        Environment.GetEnvironmentVariable("TELEKINESIS_MEMORY_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Telekinesis", "perceptual-memory");

    public VisionMemory(IRasterCodec codec, string? dir = null)
    {
        _codec = codec;
        _dir = dir ?? DefaultDir;
        Directory.CreateDirectory(Path.Combine(_dir, "crops"));
        _parseCache = LoadJsonl<ParseCacheEntry>(ParseCachePath);
        _anchors = LoadJsonl<Anchor>(AnchorsPath);
    }

    private string ParseCachePath => Path.Combine(_dir, "parse-cache.jsonl");
    private string AnchorsPath => Path.Combine(_dir, "anchors.jsonl");

    // ---- Parse cache ----

    /// <summary>Returns the cached elements when this screen was parsed before, else null.</summary>
    public IReadOnlyList<VisionElement>? TryRecallParse(string appKey, ScreenImage image)
    {
        var hash = PerceptualHash.DHash64(_codec.DecodeGray(image.PngData));
        lock (_gate)
        {
            var hit = _parseCache.FirstOrDefault(e =>
                e.AppKey == appKey && e.Width == image.Width && e.Height == image.Height &&
                PerceptualHash.HammingDistance(e.Hash, hash) <= PerceptualHash.SameScreenThreshold);
            return hit is null ? null
                : JsonSerializer.Deserialize<List<VisionElement>>(hit.ElementsJson, Json);
        }
    }

    public void StoreParse(string appKey, ScreenImage image, IReadOnlyList<VisionElement> elements)
    {
        var hash = PerceptualHash.DHash64(_codec.DecodeGray(image.PngData));
        var entry = new ParseCacheEntry(appKey, image.Width, image.Height, hash,
            JsonSerializer.Serialize(elements, Json), DateTimeOffset.UtcNow);
        lock (_gate)
        {
            // Replace any near-duplicate for the same app/size so the cache stays small.
            _parseCache.RemoveAll(e => e.AppKey == appKey && e.Width == image.Width && e.Height == image.Height &&
                PerceptualHash.HammingDistance(e.Hash, hash) <= PerceptualHash.SameScreenThreshold);
            _parseCache.Add(entry);
            RewriteJsonl(ParseCachePath, _parseCache);
        }
    }

    // ---- Anchors ----

    /// <summary>
    /// Remember an element the agent just acted on. <paramref name="imageOrigin"/> is the
    /// screen position of the source image's top-left (region captures); element bounds
    /// are absolute screen pixels; <paramref name="windowRect"/> is the owning app window.
    /// </summary>
    public Anchor? RecordAnchor(string appKey, Bounds windowRect, VisionElement element,
        ScreenImage source, (int X, int Y) imageOrigin)
    {
        // Don't hoard duplicates: the same control re-learned on every use would
        // bloat the store and the exported dataset. Same app + caption + roughly
        // the same window-relative spot = the anchor we already have.
        var cx = (element.Bounds.X + element.Bounds.Width / 2.0 - windowRect.X) / windowRect.Width;
        var cy = (element.Bounds.Y + element.Bounds.Height / 2.0 - windowRect.Y) / windowRect.Height;
        lock (_gate)
        {
            var existing = _anchors.FirstOrDefault(a => a.AppKey == appKey && a.Caption == element.Content
                && Math.Abs(a.NormCx - cx) < 0.03 && Math.Abs(a.NormCy - cy) < 0.03);
            if (existing is not null) return existing;
        }

        var id = Guid.NewGuid().ToString("n")[..12];
        var cropRegion = new Bounds(
            element.Bounds.X - imageOrigin.X, element.Bounds.Y - imageOrigin.Y,
            element.Bounds.Width, element.Bounds.Height);
        var cropPng = _codec.CropPng(source.PngData, cropRegion);

        // A near-flat crop (blank background) template-matches practically anywhere —
        // a false anchor waiting to happen. Only visually distinctive targets are kept.
        if (StdDev(_codec.DecodeGray(cropPng)) < 6.0) return null;

        var cropFile = Path.Combine("crops", $"{id}.png");
        File.WriteAllBytes(Path.Combine(_dir, cropFile), cropPng);

        var anchor = new Anchor(
            Id: id, AppKey: appKey, Caption: element.Content, Type: element.Type,
            NormCx: (element.Bounds.X + element.Bounds.Width / 2.0 - windowRect.X) / windowRect.Width,
            NormCy: (element.Bounds.Y + element.Bounds.Height / 2.0 - windowRect.Y) / windowRect.Height,
            NormW: (double)element.Bounds.Width / windowRect.Width,
            NormH: (double)element.Bounds.Height / windowRect.Height,
            CropFile: cropFile, Hits: 0, Misses: 0, LastSeen: DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _anchors.Add(anchor);
            RewriteJsonl(AnchorsPath, _anchors);
        }
        return anchor;
    }

    /// <summary>
    /// Re-locate this app's remembered targets on the live screen. Hits refresh the
    /// anchor (position drift adapts); misses decay it toward eviction.
    /// </summary>
    public IReadOnlyList<RecalledTarget> Recall(string appKey, Bounds windowRect,
        ScreenImage screen, (int X, int Y) imageOrigin)
    {
        List<Anchor> candidates;
        lock (_gate) candidates = _anchors.Where(a => a.AppKey == appKey).ToList();
        if (candidates.Count == 0) return [];

        var hay = _codec.DecodeGray(screen.PngData);
        var results = new List<RecalledTarget>();
        var changed = false;

        foreach (var a in candidates)
        {
            var cropPath = Path.Combine(_dir, a.CropFile);
            if (!File.Exists(cropPath)) { Retire(a); changed = true; continue; }
            var needle = _codec.DecodeGray(File.ReadAllBytes(cropPath));

            // Expected top-left in screen coords from the *current* window rect.
            var ex = (int)(windowRect.X + a.NormCx * windowRect.Width - needle.Width / 2.0) - imageOrigin.X;
            var ey = (int)(windowRect.Y + a.NormCy * windowRect.Height - needle.Height / 2.0) - imageOrigin.Y;
            var (mx, my, score) = TemplateMatcher.FindNear(hay, needle, ex, ey, RecallRadius);

            Anchor updated;
            if (score >= TemplateMatcher.MinScore)
            {
                var bounds = new Bounds(mx + imageOrigin.X, my + imageOrigin.Y, needle.Width, needle.Height);
                results.Add(new RecalledTarget(a.Id, a.Caption, a.Type, bounds, Math.Round(score, 3)));
                updated = a with
                {
                    Hits = a.Hits + 1,
                    LastSeen = DateTimeOffset.UtcNow,
                    NormCx = (bounds.X + bounds.Width / 2.0 - windowRect.X) / windowRect.Width,
                    NormCy = (bounds.Y + bounds.Height / 2.0 - windowRect.Y) / windowRect.Height,
                };
            }
            else
            {
                updated = a with { Misses = a.Misses + 1 };
            }

            lock (_gate)
            {
                var i = _anchors.FindIndex(x => x.Id == a.Id);
                if (i < 0) continue;
                if (updated.Misses >= EvictAfterMisses && updated.Misses > updated.Hits)
                {
                    _anchors.RemoveAt(i);
                    try { File.Delete(cropPath); } catch { }
                }
                else
                {
                    _anchors[i] = updated;
                }
                changed = true;
            }
        }

        if (changed) lock (_gate) RewriteJsonl(AnchorsPath, _anchors);
        return results;
    }

    private static double StdDev(GrayImage img)
    {
        double mean = 0;
        foreach (var p in img.Pixels) mean += p;
        mean /= img.Pixels.Length;
        double var = 0;
        foreach (var p in img.Pixels) { var d = p - mean; var += d * d; }
        return Math.Sqrt(var / img.Pixels.Length);
    }

    private void Retire(Anchor a)
    {
        lock (_gate) _anchors.RemoveAll(x => x.Id == a.Id);
    }

    // ---- Introspection / dataset export ----

    public (int ParseEntries, int Anchors, string Dir) Stats()
    {
        lock (_gate) return (_parseCache.Count, _anchors.Count, _dir);
    }

    /// <summary>
    /// Dump the memory as a training-ready dataset: grounded crops with captions,
    /// normalized boxes, and verified-use counts — the raw material for fine-tuning
    /// a local grounding model / adapter on the user's own applications.
    /// </summary>
    public int Export(string outDir)
    {
        Directory.CreateDirectory(Path.Combine(outDir, "crops"));
        List<Anchor> anchors;
        lock (_gate) anchors = _anchors.ToList();
        using var writer = new StreamWriter(Path.Combine(outDir, "dataset.jsonl"));
        var exported = 0;
        foreach (var a in anchors)
        {
            var src = Path.Combine(_dir, a.CropFile);
            if (!File.Exists(src)) continue;
            File.Copy(src, Path.Combine(outDir, a.CropFile), overwrite: true);
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                image = a.CropFile.Replace('\\', '/'),
                caption = a.Caption,
                type = a.Type,
                app = a.AppKey,
                bbox_normalized = new[] { a.NormCx, a.NormCy, a.NormW, a.NormH },
                hits = a.Hits,
                misses = a.Misses,
                last_seen = a.LastSeen,
            }, Json));
            exported++;
        }
        return exported;
    }

    // ---- JSONL plumbing ----

    private static List<T> LoadJsonl<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var list = new List<T>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<T>(line, Json) is { } item) list.Add(item); }
            catch (JsonException) { /* skip corrupt line */ }
        }
        return list;
    }

    private static void RewriteJsonl<T>(string path, IEnumerable<T> items) =>
        File.WriteAllLines(path, items.Select(i => JsonSerializer.Serialize(i, Json)));
}
