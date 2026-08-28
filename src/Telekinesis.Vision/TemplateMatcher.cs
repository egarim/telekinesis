namespace Telekinesis.Vision;

/// <summary>
/// Re-locates a remembered crop on a live screen via normalized cross-correlation.
/// Pure managed math — no ML. Kept fast by searching only around the position the
/// anchor expects, not the whole screen.
/// </summary>
public static class TemplateMatcher
{
    /// <summary>A match is only trusted at or above this correlation score.</summary>
    public const double MinScore = 0.80;

    /// <summary>
    /// Search <paramref name="hay"/> for <paramref name="needle"/> near expected
    /// top-left (<paramref name="ex"/>,<paramref name="ey"/>), within ±<paramref name="radius"/> px.
    /// Returns the best top-left position and its NCC score (-1..1).
    /// </summary>
    public static (int X, int Y, double Score) FindNear(GrayImage hay, GrayImage needle, int ex, int ey, int radius)
    {
        var x0 = Math.Max(0, ex - radius);
        var y0 = Math.Max(0, ey - radius);
        var x1 = Math.Min(hay.Width - needle.Width, ex + radius);
        var y1 = Math.Min(hay.Height - needle.Height, ey + radius);
        if (x1 < x0 || y1 < y0) return (0, 0, double.MinValue);

        // Precompute needle stats once.
        double nMean = 0;
        foreach (var p in needle.Pixels) nMean += p;
        nMean /= needle.Pixels.Length;
        double nVar = 0;
        foreach (var p in needle.Pixels) { var d = p - nMean; nVar += d * d; }
        if (nVar < 1e-9) return (0, 0, double.MinValue); // flat template matches anything

        var best = (X: 0, Y: 0, Score: double.MinValue);
        // Coarse-to-fine: stride 2 sweep, then refine the winner's 3x3 neighborhood.
        for (var pass = 0; pass < 2; pass++)
        {
            int sx0 = x0, sy0 = y0, sx1 = x1, sy1 = y1, stride = 2;
            if (pass == 1)
            {
                sx0 = Math.Max(x0, best.X - 1); sx1 = Math.Min(x1, best.X + 1);
                sy0 = Math.Max(y0, best.Y - 1); sy1 = Math.Min(y1, best.Y + 1);
                stride = 1;
            }
            for (var y = sy0; y <= sy1; y += stride)
            for (var x = sx0; x <= sx1; x += stride)
            {
                var score = Ncc(hay, needle, x, y, nMean, nVar);
                if (score > best.Score) best = (x, y, score);
            }
        }
        return best;
    }

    private static double Ncc(GrayImage hay, GrayImage needle, int ox, int oy, double nMean, double nVar)
    {
        double hMean = 0;
        for (var y = 0; y < needle.Height; y++)
        for (var x = 0; x < needle.Width; x++)
            hMean += hay.At(ox + x, oy + y);
        hMean /= needle.Pixels.Length;

        double cross = 0, hVar = 0;
        for (var y = 0; y < needle.Height; y++)
        for (var x = 0; x < needle.Width; x++)
        {
            var hd = hay.At(ox + x, oy + y) - hMean;
            var nd = needle.At(x, y) - nMean;
            cross += hd * nd;
            hVar += hd * hd;
        }
        if (hVar < 1e-9) return double.MinValue;
        return cross / Math.Sqrt(hVar * nVar);
    }
}
