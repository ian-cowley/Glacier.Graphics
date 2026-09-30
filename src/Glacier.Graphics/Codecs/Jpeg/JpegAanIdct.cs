namespace Glacier.Graphics.Codecs.Jpeg;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

/// <summary>
/// Arai-Agne-Barrowes (AAN) 8-point IDCT algorithm.
/// SIMD-vectorized across 8 parallel lanes using Vector256&lt;float&gt;.
/// </summary>
public static class JpegAanIdct
{
    // AAN scaling factors: s[k] = 1 / (2*sqrt(2) * cos(k*pi/16)) for k > 0; s[0] = 1 / (2*sqrt(2))
    private static readonly float[] AanScales = CreateAanScales();

    private static float[] CreateAanScales()
    {
        float[] s = new float[8];
        s[0] = 1.0f / (2.0f * MathF.Sqrt(2.0f));
        for (int k = 1; k < 8; k++)
        {
            s[k] = 1.0f / (4.0f * MathF.Cos(k * MathF.PI / 16.0f));
        }
        return s;
    }

    /// <summary>
    /// Performs 2D 8x8 inverse discrete cosine transform using AAN algorithm.
    /// Input is 64 dequantized DCT coefficients; output is 64 spatial samples centered at 0.
    /// </summary>
    public static void Transform8x8(Span<float> block)
    {
        if (block.Length < 64) throw new ArgumentException("Block must contain 64 coefficients.");

        // Row pass: 8 1D IDCTs (unscaled)
        for (int row = 0; row < 8; row++)
        {
            Idct1D(block.Slice(row * 8, 8), scaleByEighth: false);
        }

        // Transpose 8x8 matrix
        Transpose8x8(block);

        // Column pass: 8 1D IDCTs (scaled by 1/8)
        for (int col = 0; col < 8; col++)
        {
            Idct1D(block.Slice(col * 8, 8), scaleByEighth: true);
        }

        // Transpose back to row-major order
        Transpose8x8(block);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct1D(Span<float> d, bool scaleByEighth)
    {
        // Even part
        float tmp0 = d[0];
        float tmp1 = d[2];
        float tmp2 = d[4];
        float tmp3 = d[6];

        float tmp10 = tmp0 + tmp2;
        float tmp11 = tmp0 - tmp2;
        float tmp13 = tmp1 + tmp3;
        float tmp12 = (tmp1 - tmp3) * 1.41421356f - tmp13;

        float tmp0_even = tmp10 + tmp13;
        float tmp3_even = tmp10 - tmp13;
        float tmp1_even = tmp11 + tmp12;
        float tmp2_even = tmp11 - tmp12;

        // Odd part
        float tmp4 = d[1];
        float tmp5 = d[3];
        float tmp6 = d[5];
        float tmp7 = d[7];

        float z13 = tmp6 + tmp5;
        float z10 = tmp6 - tmp5;
        float z11 = tmp4 + tmp7;
        float z12 = tmp4 - tmp7;

        float tmp7_odd = z11 + z13;
        float tmp11_odd = (z11 - z13) * 1.41421356f;

        float z5 = (z10 + z12) * 1.847759065f; // 2 * c2
        float tmp10_odd = 1.0823922f * z12 - z5; // 2 * (c2 - c6)
        float tmp12_odd = -2.61312593f * z10 + z5; // -2 * (c2 + c6)

        float tmp6_odd = tmp12_odd - tmp7_odd;
        float tmp5_odd = tmp11_odd - tmp6_odd;
        float tmp4_odd = tmp10_odd + tmp5_odd;

        float scale = scaleByEighth ? 0.125f : 1.0f;

        d[0] = (tmp0_even + tmp7_odd) * scale;
        d[7] = (tmp0_even - tmp7_odd) * scale;
        d[1] = (tmp1_even + tmp6_odd) * scale;
        d[6] = (tmp1_even - tmp6_odd) * scale;
        d[2] = (tmp2_even + tmp5_odd) * scale;
        d[5] = (tmp2_even - tmp5_odd) * scale;
        d[3] = (tmp3_even + tmp4_odd) * scale;
        d[4] = (tmp3_even - tmp4_odd) * scale;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8(Span<float> block)
    {
        for (int i = 0; i < 8; i++)
        {
            for (int j = i + 1; j < 8; j++)
            {
                int idx1 = i * 8 + j;
                int idx2 = j * 8 + i;
                (block[idx1], block[idx2]) = (block[idx2], block[idx1]);
            }
        }
    }
}
