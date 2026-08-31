using System;
using System.Numerics;

namespace Robust.Client3D.Assets;

public sealed record TextureImageData3D
{
    public int Width { get; }
    public int Height { get; }
    public byte[] RgbaPixels { get; }

    public TextureImageData3D(int width, int height, byte[] rgbaPixels)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));

        ArgumentNullException.ThrowIfNull(rgbaPixels);
        var expectedLength = checked(width * height * 4);
        if (rgbaPixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"RGBA texture data must contain exactly {expectedLength} bytes.",
                nameof(rgbaPixels));
        }

        Width = width;
        Height = height;
        RgbaPixels = rgbaPixels;
    }
}

public sealed record MaterialData3D(
    Vector4 BaseColorFactor,
    TextureImageData3D? BaseColorTexture)
{
    public static MaterialData3D Default { get; } = new(Vector4.One, null);
}
