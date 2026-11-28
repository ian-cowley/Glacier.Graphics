namespace Glacier.Graphics.Raster;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Shared straight-alpha Porter-Duff SRC_OVER pixel compositing helpers.
/// </summary>
internal static class ColorBlend
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void BlendPixel(ref Rgba32 dst, Rgba32 src, float coverage)
    {
        float sa = (src.A / 255.0f) * coverage;
        if (sa <= 0f) return;

        if (sa >= 1.0f)
        {
            dst = new Rgba32(src.R, src.G, src.B, 255);
            return;
        }

        if (dst.A == 255)
        {
            float invA = 1.0f - sa;
            byte outR = (byte)Math.Clamp((int)MathF.Round(src.R * sa + dst.R * invA), 0, 255);
            byte outG = (byte)Math.Clamp((int)MathF.Round(src.G * sa + dst.G * invA), 0, 255);
            byte outB = (byte)Math.Clamp((int)MathF.Round(src.B * sa + dst.B * invA), 0, 255);
            dst = new Rgba32(outR, outG, outB, 255);
            return;
        }

        if (dst.A == 0)
        {
            byte outA0 = (byte)Math.Clamp((int)MathF.Round(sa * 255.0f), 0, 255);
            dst = outA0 == 0 ? Rgba32.Transparent : new Rgba32(src.R, src.G, src.B, outA0);
            return;
        }

        float da = dst.A / 255.0f;
        float dstFactor = da * (1.0f - sa);
        float outA = sa + dstFactor;
        if (outA <= 0f)
        {
            dst = Rgba32.Transparent;
            return;
        }

        float invOutA = 1.0f / outA;
        byte outR2 = (byte)Math.Clamp((int)MathF.Round((src.R * sa + dst.R * dstFactor) * invOutA), 0, 255);
        byte outG2 = (byte)Math.Clamp((int)MathF.Round((src.G * sa + dst.G * dstFactor) * invOutA), 0, 255);
        byte outB2 = (byte)Math.Clamp((int)MathF.Round((src.B * sa + dst.B * dstFactor) * invOutA), 0, 255);
        byte outByteA = (byte)Math.Clamp((int)MathF.Round(outA * 255.0f), 0, 255);

        dst = new Rgba32(outR2, outG2, outB2, outByteA);
    }
}
