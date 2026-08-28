namespace Telekinesis.Vision;

/// <summary>
/// 64-bit difference hash (dHash): box-downsample to 9x8 grayscale, emit one bit
/// per horizontal neighbor comparison. Robust to compression noise and small
/// rendering differences; a cursor or blinking caret flips only a few bits, a
/// different screen flips ~half. Distances ≤ <see cref="SameScreenThreshold"/>
/// are treated as "the same screen".
/// </summary>
public static class PerceptualHash
{
    public const int SameScreenThreshold = 6;

    public static ulong DHash64(GrayImage image)
    {
        // Box-sample to a 9x8 grid.
        Span<double> grid = stackalloc double[72];
        for (var gy = 0; gy < 8; gy++)
        for (var gx = 0; gx < 9; gx++)
        {
            int x0 = gx * image.Width / 9, x1 = Math.Max(x0 + 1, (gx + 1) * image.Width / 9);
            int y0 = gy * image.Height / 8, y1 = Math.Max(y0 + 1, (gy + 1) * image.Height / 8);
            long sum = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                sum += image.At(Math.Min(x, image.Width - 1), Math.Min(y, image.Height - 1));
            grid[gy * 9 + gx] = (double)sum / ((x1 - x0) * (y1 - y0));
        }

        ulong hash = 0;
        var bit = 0;
        for (var gy = 0; gy < 8; gy++)
        for (var gx = 0; gx < 8; gx++, bit++)
            if (grid[gy * 9 + gx] < grid[gy * 9 + gx + 1])
                hash |= 1UL << bit;
        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
