using Telekinesis.Abstractions;

namespace Telekinesis.Vision;

/// <summary>An 8-bit grayscale raster. The unit all memory math runs on.</summary>
public sealed record GrayImage(byte[] Pixels, int Width, int Height)
{
    public byte At(int x, int y) => Pixels[y * Width + x];
}

/// <summary>
/// Platform seam for the pixel operations perceptual memory needs. The Vision
/// project stays dependency-free and cross-platform; each OS backend supplies a
/// codec (Windows: System.Drawing). Everything downstream — hashing, template
/// matching, crops — is pure managed math on <see cref="GrayImage"/>.
/// </summary>
public interface IRasterCodec
{
    /// <summary>Decode a PNG to 8-bit grayscale.</summary>
    GrayImage DecodeGray(byte[] png);

    /// <summary>Crop a region out of a PNG, returned as PNG. Region is clamped to the image.</summary>
    byte[] CropPng(byte[] png, Bounds region);
}
