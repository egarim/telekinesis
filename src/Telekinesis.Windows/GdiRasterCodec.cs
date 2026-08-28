using System.Drawing;
using System.Drawing.Imaging;
using Telekinesis.Abstractions;
using Telekinesis.Vision;

namespace Telekinesis.Windows;

/// <summary>Windows implementation of the perceptual-memory raster seam, on GDI+.</summary>
public sealed class GdiRasterCodec : IRasterCodec
{
    public GrayImage DecodeGray(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        var pixels = new byte[bmp.Width * bmp.Height];
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (var y = 0; y < bmp.Height; y++)
                {
                    var row = (byte*)data.Scan0 + y * data.Stride;
                    for (var x = 0; x < bmp.Width; x++)
                    {
                        // BGRA → Rec.601 luma.
                        var b = row[x * 4];
                        var g = row[x * 4 + 1];
                        var r = row[x * 4 + 2];
                        pixels[y * bmp.Width + x] = (byte)((299 * r + 587 * g + 114 * b) / 1000);
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return new GrayImage(pixels, bmp.Width, bmp.Height);
    }

    public byte[] CropPng(byte[] png, Bounds region)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        var x = Math.Clamp(region.X, 0, bmp.Width - 1);
        var y = Math.Clamp(region.Y, 0, bmp.Height - 1);
        var w = Math.Clamp(region.Width, 1, bmp.Width - x);
        var h = Math.Clamp(region.Height, 1, bmp.Height - y);
        using var crop = bmp.Clone(new Rectangle(x, y, w, h), PixelFormat.Format32bppArgb);
        using var outMs = new MemoryStream();
        crop.Save(outMs, ImageFormat.Png);
        return outMs.ToArray();
    }
}
