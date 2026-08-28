using Telekinesis.Abstractions;
using Telekinesis.Vision;

namespace Telekinesis.Cli;

/// <summary>
/// Session glue for perceptual memory: owns the <see cref="VisionMemory"/> store
/// (when the platform provides a raster codec) and remembers the most recent
/// parse so a follow-up click_at can turn the element it hit into an anchor.
/// </summary>
public sealed class VisionMemoryService
{
    public sealed record LastParse(string AppKey, Bounds WindowRect, ScreenImage Image,
        (int X, int Y) Origin, IReadOnlyList<VisionElement> Elements);

    private readonly Lazy<VisionMemory?> _memory = new(Create);

    /// <summary>The most recent parse_screen result, kept for anchor recording.</summary>
    public LastParse? Last { get; set; }

    public VisionMemory? Memory => _memory.Value;

    private static VisionMemory? Create()
    {
#if WINDOWS
        return new VisionMemory(new Telekinesis.Windows.GdiRasterCodec());
#else
        return null; // no raster codec on this platform yet
#endif
    }

    /// <summary>With TELEKINESIS_LEARN=1, successful a11y actions also teach the vision
    /// tier: the acted-on element is recorded as a perceptual anchor. Opt-in because it
    /// costs a region capture per action.</summary>
    public static bool LearnEnabled =>
        Environment.GetEnvironmentVariable("TELEKINESIS_LEARN") is "1" or "true";

    /// <summary>
    /// Record a perceptual anchor for an element the accessibility tier just acted on
    /// successfully — the reliable tier teaching the fallback tier. A11y-verified
    /// captions/bounds are better dataset labels than vision-derived ones.
    /// </summary>
    public async Task<Anchor?> LearnFromElementAsync(IAccessibilityBackend backend, ElementRef reference, CancellationToken ct = default)
    {
        if (!LearnEnabled || Memory is null || backend is not IScreenCaptureBackend capture) return null;
        try
        {
            var element = await backend.ReadElementAsync(reference, ct);
            if (element.Bounds is not { } bounds) return null;

            // Never learn from a covered element: the crop would be whatever window is
            // on top, and recall would then "find" those wrong pixels with confidence.
            if (!ElementPixelsVisible(reference, bounds)) return null;

            var tree = await backend.GetTreeAsync(reference.ApplicationId, 1, ct);
            var window = tree.Children?.FirstOrDefault(w => w.Bounds is not null)?.Bounds;
            if (window is null) return null;

            var image = await capture.CaptureScreenAsync(window, ct);
            var visionElement = new VisionElement(
                Type: $"a11y-{element.Role.ToString().ToLowerInvariant()}",
                Content: element.Name,
                Interactive: true,
                Bounds: bounds);
            var anchor = Memory.RecordAnchor(tree.Name ?? reference.ApplicationId, window,
                visionElement, image, (window.X, window.Y));
            if (anchor is not null)
                Console.Error.WriteLine($"[telekinesis] learned anchor {anchor.Id} \"{anchor.Caption}\" from a11y action");
            return anchor;
        }
        catch (Exception e) when (e is StaleElementException or InvalidOperationException or ArgumentException)
        {
            return null; // learning must never break the action that succeeded
        }
    }

    /// <summary>True when the element's center pixel belongs to its own application's
    /// window — i.e. the pixels we would crop are actually the element's.</summary>
    private static bool ElementPixelsVisible(ElementRef reference, Bounds bounds)
    {
#if WINDOWS
        try
        {
            var hwnd = WindowFromPoint(new Point
            {
                X = bounds.X + bounds.Width / 2,
                Y = bounds.Y + bounds.Height / 2,
            });
            if (hwnd == 0) return false;
            _ = GetWindowThreadProcessId(GetAncestor(hwnd, 2 /* GA_ROOT */), out var pid);
            var appPid = reference.ApplicationId.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(reference.ApplicationId[4..], out var parsed) ? parsed : -1;
            return appPid > 0 && pid == appPid;
        }
        catch
        {
            return false; // can't prove visibility → don't learn
        }
#else
        return true; // no per-pixel hit test on this platform yet
#endif
    }

#if WINDOWS
    private struct Point { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
#endif

    /// <summary>
    /// Called after a successful click_at: if the point falls inside an element of
    /// the most recent parse, that element just proved itself useful — remember it.
    /// </summary>
    public Anchor? OnClickedAt(int x, int y)
    {
        if (Memory is null || Last is null) return null;
        var el = Last.Elements.FirstOrDefault(e =>
            x >= e.Bounds.X && x < e.Bounds.X + e.Bounds.Width &&
            y >= e.Bounds.Y && y < e.Bounds.Y + e.Bounds.Height);
        return el is null ? null : Memory.RecordAnchor(Last.AppKey, Last.WindowRect, el, Last.Image, Last.Origin);
    }
}
