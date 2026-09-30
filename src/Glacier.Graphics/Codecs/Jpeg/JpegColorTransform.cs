namespace Glacier.Graphics.Codecs.Jpeg;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

/// <summary>
/// Hardware-accelerated SIMD YCbCr to RGB color space conversion.
/// </summary>
public static class JpegColorTransform
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba32 ConvertPixel(float y, float cb, float cr)
    {
        float cbShift = cb - 128.0f;
        float crShift = cr - 128.0f;

        float r = y + 1.402f * crShift;
        float g = y - 0.344136f * cbShift - 0.714136f * crShift;
        float b = y + 1.772f * cbShift;

        byte byteR = (byte)Math.Clamp((int)MathF.Round(r), 0, 255);
        byte byteG = (byte)Math.Clamp((int)MathF.Round(g), 0, 255);
        byte byteB = (byte)Math.Clamp((int)MathF.Round(b), 0, 255);

        return new Rgba32(byteR, byteG, byteB, 255);
    }

    /// <summary>
    /// Vectorized conversion of 64 spatial samples (8x8 block) from Y, Cb, Cr planes to RGBA32.
    /// </summary>
    public static void ConvertBlock(
        ReadOnlySpan<float> yBlock,
        ReadOnlySpan<float> cbBlock,
        ReadOnlySpan<float> crBlock,
        Span<Rgba32> outputBlock)
    {
        if (yBlock.Length < 64 || cbBlock.Length < 64 || crBlock.Length < 64 || outputBlock.Length < 64)
            throw new ArgumentException("Blocks must contain 64 samples.");

        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var v128 = Vector256.Create(128.0f);
            var vR_Cr = Vector256.Create(1.402f);
            var vG_Cb = Vector256.Create(-0.344136f);
            var vG_Cr = Vector256.Create(-0.714136f);
            var vB_Cb = Vector256.Create(1.772f);
            var vZero = Vector256<float>.Zero;
            var v255 = Vector256.Create(255.0f);

            int step = Vector256<float>.Count; // 8 floats
            for (; i <= 64 - step; i += step)
            {
                var vy = Vector256.Create(yBlock.Slice(i, step));
                var vcb = Vector256.Create(cbBlock.Slice(i, step)) - v128;
                var vcr = Vector256.Create(crBlock.Slice(i, step)) - v128;

                var vr = Vector256.Min(v255, Vector256.Max(vZero, vy + vcr * vR_Cr));
                var vg = Vector256.Min(v255, Vector256.Max(vZero, vy + vcb * vG_Cb + vcr * vG_Cr));
                var vb = Vector256.Min(v255, Vector256.Max(vZero, vy + vcb * vB_Cb));

                for (int lane = 0; lane < step; lane++)
                {
                    outputBlock[i + lane] = new Rgba32(
                        (byte)MathF.Round(vr[lane]),
                        (byte)MathF.Round(vg[lane]),
                        (byte)MathF.Round(vb[lane]),
                        255);
                }
            }
        }

        for (; i < 64; i++)
        {
            outputBlock[i] = ConvertPixel(yBlock[i], cbBlock[i], crBlock[i]);
        }
    }
}
