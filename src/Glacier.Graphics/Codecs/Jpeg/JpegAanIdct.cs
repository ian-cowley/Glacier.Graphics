namespace Glacier.Graphics.Codecs.Jpeg;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

/// <summary>
/// Arai-Agne-Barrowes (AAN) 8-point IDCT algorithm.
/// SIMD-vectorized across all 8 parallel lanes using Vector256&lt;float&gt; with in-register AVX 8x8 transpose,
/// with exact scalar fallback.
/// </summary>
public static class JpegAanIdct
{
    private static readonly float[] AanScales = CreateAanScales();

    private static float[] CreateAanScales()
    {
        float[] s = new float[8];
        s[0] = 1.0f;
        for (int k = 1; k < 8; k++)
        {
            s[k] = MathF.Sqrt(2.0f) * MathF.Cos(k * MathF.PI / 16.0f);
        }
        return s;
    }

    /// <summary>
    /// Performs 2D 8x8 inverse discrete cosine transform using the AAN algorithm.
    /// Input is 64 dequantized DCT coefficients; output is 64 spatial samples centered at 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Transform8x8(Span<float> block)
    {
        if (block.Length < 64) throw new ArgumentException("Block must contain 64 coefficients.");

        if (Vector256.IsHardwareAccelerated && Avx.IsSupported)
        {
            Transform8x8Vector256(block);
            return;
        }

        Transform8x8Scalar(block);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Transform8x8Vector256(Span<float> block)
    {
        var vColScale = Vector256.Create(AanScales);

        var r0 = Vector256.Create(block.Slice(0, 8)) * (vColScale * Vector256.Create(AanScales[0]));
        var r1 = Vector256.Create(block.Slice(8, 8)) * (vColScale * Vector256.Create(AanScales[1]));
        var r2 = Vector256.Create(block.Slice(16, 8)) * (vColScale * Vector256.Create(AanScales[2]));
        var r3 = Vector256.Create(block.Slice(24, 8)) * (vColScale * Vector256.Create(AanScales[3]));
        var r4 = Vector256.Create(block.Slice(32, 8)) * (vColScale * Vector256.Create(AanScales[4]));
        var r5 = Vector256.Create(block.Slice(40, 8)) * (vColScale * Vector256.Create(AanScales[5]));
        var r6 = Vector256.Create(block.Slice(48, 8)) * (vColScale * Vector256.Create(AanScales[6]));
        var r7 = Vector256.Create(block.Slice(56, 8)) * (vColScale * Vector256.Create(AanScales[7]));

        // Transpose rows -> columns so 8 rows execute in parallel across 8 SIMD lanes
        Transpose8x8Vector256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Idct1DVector256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, 1.0f);

        // Transpose back -> rows so 8 columns execute in parallel across 8 SIMD lanes
        Transpose8x8Vector256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Idct1DVector256(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7, 0.125f);

        r0.CopyTo(block.Slice(0, 8));
        r1.CopyTo(block.Slice(8, 8));
        r2.CopyTo(block.Slice(16, 8));
        r3.CopyTo(block.Slice(24, 8));
        r4.CopyTo(block.Slice(32, 8));
        r5.CopyTo(block.Slice(40, 8));
        r6.CopyTo(block.Slice(48, 8));
        r7.CopyTo(block.Slice(56, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct1DVector256(
        ref Vector256<float> d0, ref Vector256<float> d1, ref Vector256<float> d2, ref Vector256<float> d3,
        ref Vector256<float> d4, ref Vector256<float> d5, ref Vector256<float> d6, ref Vector256<float> d7,
        float scale)
    {
        var vSqrt2 = Vector256.Create(1.41421356f);
        var vC2x2 = Vector256.Create(1.847759065f);
        var vC2MinusC6x2 = Vector256.Create(1.0823922f);
        var vNegC2PlusC6x2 = Vector256.Create(-2.61312593f);

        // Even part
        var tmp10 = d0 + d4;
        var tmp11 = d0 - d4;
        var tmp13 = d2 + d6;
        var tmp12 = (d2 - d6) * vSqrt2 - tmp13;

        var tmp0Even = tmp10 + tmp13;
        var tmp3Even = tmp10 - tmp13;
        var tmp1Even = tmp11 + tmp12;
        var tmp2Even = tmp11 - tmp12;

        // Odd part
        var z13 = d5 + d3;
        var z10 = d5 - d3;
        var z11 = d1 + d7;
        var z12 = d1 - d7;

        var tmp7Odd = z11 + z13;
        var tmp11Odd = (z11 - z13) * vSqrt2;

        var z5 = (z10 + z12) * vC2x2;
        var tmp10Odd = vC2MinusC6x2 * z12 - z5;
        var tmp12Odd = vNegC2PlusC6x2 * z10 + z5;

        var tmp6Odd = tmp12Odd - tmp7Odd;
        var tmp5Odd = tmp11Odd - tmp6Odd;
        var tmp4Odd = -(tmp10Odd + tmp5Odd);

        if (scale == 1.0f)
        {
            d0 = tmp0Even + tmp7Odd;
            d7 = tmp0Even - tmp7Odd;
            d1 = tmp1Even + tmp6Odd;
            d6 = tmp1Even - tmp6Odd;
            d2 = tmp2Even + tmp5Odd;
            d5 = tmp2Even - tmp5Odd;
            d3 = tmp3Even + tmp4Odd;
            d4 = tmp3Even - tmp4Odd;
        }
        else
        {
            var vScale = Vector256.Create(scale);
            d0 = (tmp0Even + tmp7Odd) * vScale;
            d7 = (tmp0Even - tmp7Odd) * vScale;
            d1 = (tmp1Even + tmp6Odd) * vScale;
            d6 = (tmp1Even - tmp6Odd) * vScale;
            d2 = (tmp2Even + tmp5Odd) * vScale;
            d5 = (tmp2Even - tmp5Odd) * vScale;
            d3 = (tmp3Even + tmp4Odd) * vScale;
            d4 = (tmp3Even - tmp4Odd) * vScale;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8x8Vector256(
        ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3,
        ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        var t0 = Avx.UnpackLow(r0, r1);
        var t1 = Avx.UnpackHigh(r0, r1);
        var t2 = Avx.UnpackLow(r2, r3);
        var t3 = Avx.UnpackHigh(r2, r3);
        var t4 = Avx.UnpackLow(r4, r5);
        var t5 = Avx.UnpackHigh(r4, r5);
        var t6 = Avx.UnpackLow(r6, r7);
        var t7 = Avx.UnpackHigh(r6, r7);

        var u0 = Avx.Shuffle(t0, t2, 0x44);
        var u1 = Avx.Shuffle(t0, t2, 0xEE);
        var u2 = Avx.Shuffle(t1, t3, 0x44);
        var u3 = Avx.Shuffle(t1, t3, 0xEE);
        var u4 = Avx.Shuffle(t4, t6, 0x44);
        var u5 = Avx.Shuffle(t4, t6, 0xEE);
        var u6 = Avx.Shuffle(t5, t7, 0x44);
        var u7 = Avx.Shuffle(t5, t7, 0xEE);

        r0 = Avx.Permute2x128(u0, u4, 0x20);
        r1 = Avx.Permute2x128(u1, u5, 0x20);
        r2 = Avx.Permute2x128(u2, u6, 0x20);
        r3 = Avx.Permute2x128(u3, u7, 0x20);
        r4 = Avx.Permute2x128(u0, u4, 0x31);
        r5 = Avx.Permute2x128(u1, u5, 0x31);
        r6 = Avx.Permute2x128(u2, u6, 0x31);
        r7 = Avx.Permute2x128(u3, u7, 0x31);
    }

    private static void Transform8x8Scalar(Span<float> block)
    {
        for (int r = 0; r < 8; r++)
        {
            float sr = AanScales[r];
            int rowBase = r * 8;
            for (int c = 0; c < 8; c++)
            {
                block[rowBase + c] *= sr * AanScales[c];
            }
        }

        for (int row = 0; row < 8; row++)
        {
            Idct1D(block.Slice(row * 8, 8), scaleByEighth: false);
        }

        Transpose8x8Scalar(block);

        for (int col = 0; col < 8; col++)
        {
            Idct1D(block.Slice(col * 8, 8), scaleByEighth: true);
        }

        Transpose8x8Scalar(block);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Idct1D(Span<float> d, bool scaleByEighth)
    {
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

        float z5 = (z10 + z12) * 1.847759065f;
        float tmp10_odd = 1.0823922f * z12 - z5;
        float tmp12_odd = -2.61312593f * z10 + z5;

        float tmp6_odd = tmp12_odd - tmp7_odd;
        float tmp5_odd = tmp11_odd - tmp6_odd;
        float tmp4_odd = -(tmp10_odd + tmp5_odd);

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
    private static void Transpose8x8Scalar(Span<float> block)
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
