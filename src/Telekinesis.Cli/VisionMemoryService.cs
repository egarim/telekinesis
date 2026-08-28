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
