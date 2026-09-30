namespace Glacier.Graphics.Vector;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

/// <summary>
/// Hardware-accelerated SIMD Bézier curve evaluation kernels.
/// Supports AVX-512 (16 lanes), Vector256 (8 lanes), and scalar fallback.
/// </summary>
public static class BezierKernels
{
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void EvaluateCubicAvx512(
        float p0x, float p0y, float p1x, float p1y,
        float p2x, float p2y, float p3x, float p3y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        if (tValues.Length > outX.Length || tValues.Length > outY.Length)
            throw new ArgumentException("Output spans must be at least as long as tValues.");

        Vector512<float> vP0x = Vector512.Create(p0x);
        Vector512<float> vP0y = Vector512.Create(p0y);
        Vector512<float> vP1x = Vector512.Create(p1x);
        Vector512<float> vP1y = Vector512.Create(p1y);
        Vector512<float> vP2x = Vector512.Create(p2x);
        Vector512<float> vP2y = Vector512.Create(p2y);
        Vector512<float> vP3x = Vector512.Create(p3x);
        Vector512<float> vP3y = Vector512.Create(p3y);
        Vector512<float> vOne  = Vector512.Create(1.0f);
        Vector512<float> vThree = Vector512.Create(3.0f);

        int i = 0;
        int vectorStep = Vector512<float>.Count; // 16 floats
        for (; i <= tValues.Length - vectorStep; i += vectorStep)
        {
            Vector512<float> t = Vector512.Create(tValues.Slice(i, vectorStep));
            Vector512<float> u = vOne - t;
            Vector512<float> tt = t * t;
            Vector512<float> uu = u * u;
            Vector512<float> uuu = uu * u;
            Vector512<float> ttt = tt * t;

            Vector512<float> c0 = uuu;
            Vector512<float> c1 = vThree * uu * t;
            Vector512<float> c2 = vThree * u * tt;
            Vector512<float> c3 = ttt;

            Vector512<float> rx = c0 * vP0x + c1 * vP1x + c2 * vP2x + c3 * vP3x;
            Vector512<float> ry = c0 * vP0y + c1 * vP1y + c2 * vP2y + c3 * vP3y;

            rx.CopyTo(outX.Slice(i, vectorStep));
            ry.CopyTo(outY.Slice(i, vectorStep));
        }

        // Scalar remainder loop
        for (; i < tValues.Length; i++)
        {
            float t = tValues[i];
            float u = 1.0f - t;
            float c0 = u * u * u;
            float c1 = 3.0f * u * u * t;
            float c2 = 3.0f * u * t * t;
            float c3 = t * t * t;
            outX[i] = c0 * p0x + c1 * p1x + c2 * p2x + c3 * p3x;
            outY[i] = c0 * p0y + c1 * p1y + c2 * p2y + c3 * p3y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void EvaluateCubicVector256(
        float p0x, float p0y, float p1x, float p1y,
        float p2x, float p2y, float p3x, float p3y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        if (tValues.Length > outX.Length || tValues.Length > outY.Length)
            throw new ArgumentException("Output spans must be at least as long as tValues.");

        Vector256<float> vP0x = Vector256.Create(p0x);
        Vector256<float> vP0y = Vector256.Create(p0y);
        Vector256<float> vP1x = Vector256.Create(p1x);
        Vector256<float> vP1y = Vector256.Create(p1y);
        Vector256<float> vP2x = Vector256.Create(p2x);
        Vector256<float> vP2y = Vector256.Create(p2y);
        Vector256<float> vP3x = Vector256.Create(p3x);
        Vector256<float> vP3y = Vector256.Create(p3y);
        Vector256<float> vOne  = Vector256.Create(1.0f);
        Vector256<float> vThree = Vector256.Create(3.0f);

        int i = 0;
        int vectorStep = Vector256<float>.Count; // 8 floats
        for (; i <= tValues.Length - vectorStep; i += vectorStep)
        {
            Vector256<float> t = Vector256.Create(tValues.Slice(i, vectorStep));
            Vector256<float> u = vOne - t;
            Vector256<float> tt = t * t;
            Vector256<float> uu = u * u;
            Vector256<float> uuu = uu * u;
            Vector256<float> ttt = tt * t;

            Vector256<float> c0 = uuu;
            Vector256<float> c1 = vThree * uu * t;
            Vector256<float> c2 = vThree * u * tt;
            Vector256<float> c3 = ttt;

            Vector256<float> rx = c0 * vP0x + c1 * vP1x + c2 * vP2x + c3 * vP3x;
            Vector256<float> ry = c0 * vP0y + c1 * vP1y + c2 * vP2y + c3 * vP3y;

            rx.CopyTo(outX.Slice(i, vectorStep));
            ry.CopyTo(outY.Slice(i, vectorStep));
        }

        // Scalar remainder loop
        for (; i < tValues.Length; i++)
        {
            float t = tValues[i];
            float u = 1.0f - t;
            float c0 = u * u * u;
            float c1 = 3.0f * u * u * t;
            float c2 = 3.0f * u * t * t;
            float c3 = t * t * t;
            outX[i] = c0 * p0x + c1 * p1x + c2 * p2x + c3 * p3x;
            outY[i] = c0 * p0y + c1 * p1y + c2 * p2y + c3 * p3y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void EvaluateCubicScalar(
        float p0x, float p0y, float p1x, float p1y,
        float p2x, float p2y, float p3x, float p3y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        for (int i = 0; i < tValues.Length; i++)
        {
            float t = tValues[i];
            float u = 1.0f - t;
            float c0 = u * u * u;
            float c1 = 3.0f * u * u * t;
            float c2 = 3.0f * u * t * t;
            float c3 = t * t * t;
            outX[i] = c0 * p0x + c1 * p1x + c2 * p2x + c3 * p3x;
            outY[i] = c0 * p0y + c1 * p1y + c2 * p2y + c3 * p3y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void EvaluateQuadraticAvx512(
        float p0x, float p0y, float p1x, float p1y, float p2x, float p2y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        Vector512<float> vP0x = Vector512.Create(p0x);
        Vector512<float> vP0y = Vector512.Create(p0y);
        Vector512<float> vP1x = Vector512.Create(p1x);
        Vector512<float> vP1y = Vector512.Create(p1y);
        Vector512<float> vP2x = Vector512.Create(p2x);
        Vector512<float> vP2y = Vector512.Create(p2y);
        Vector512<float> vOne  = Vector512.Create(1.0f);
        Vector512<float> vTwo  = Vector512.Create(2.0f);

        int i = 0;
        int vectorStep = Vector512<float>.Count;
        for (; i <= tValues.Length - vectorStep; i += vectorStep)
        {
            Vector512<float> t = Vector512.Create(tValues.Slice(i, vectorStep));
            Vector512<float> u = vOne - t;
            Vector512<float> uu = u * u;
            Vector512<float> tt = t * t;
            Vector512<float> ut2 = vTwo * u * t;

            Vector512<float> rx = uu * vP0x + ut2 * vP1x + tt * vP2x;
            Vector512<float> ry = uu * vP0y + ut2 * vP1y + tt * vP2y;

            rx.CopyTo(outX.Slice(i, vectorStep));
            ry.CopyTo(outY.Slice(i, vectorStep));
        }

        for (; i < tValues.Length; i++)
        {
            float t = tValues[i];
            float u = 1.0f - t;
            outX[i] = u * u * p0x + 2.0f * u * t * p1x + t * t * p2x;
            outY[i] = u * u * p0y + 2.0f * u * t * p1y + t * t * p2y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void EvaluateQuadraticVector256(
        float p0x, float p0y, float p1x, float p1y, float p2x, float p2y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        Vector256<float> vP0x = Vector256.Create(p0x);
        Vector256<float> vP0y = Vector256.Create(p0y);
        Vector256<float> vP1x = Vector256.Create(p1x);
        Vector256<float> vP1y = Vector256.Create(p1y);
        Vector256<float> vP2x = Vector256.Create(p2x);
        Vector256<float> vP2y = Vector256.Create(p2y);
        Vector256<float> vOne  = Vector256.Create(1.0f);
        Vector256<float> vTwo  = Vector256.Create(2.0f);

        int i = 0;
        int vectorStep = Vector256<float>.Count;
        for (; i <= tValues.Length - vectorStep; i += vectorStep)
        {
            Vector256<float> t = Vector256.Create(tValues.Slice(i, vectorStep));
            Vector256<float> u = vOne - t;
            Vector256<float> uu = u * u;
            Vector256<float> tt = t * t;
            Vector256<float> ut2 = vTwo * u * t;

            Vector256<float> rx = uu * vP0x + ut2 * vP1x + tt * vP2x;
            Vector256<float> ry = uu * vP0y + ut2 * vP1y + tt * vP2y;

            rx.CopyTo(outX.Slice(i, vectorStep));
            ry.CopyTo(outY.Slice(i, vectorStep));
        }

        for (; i < tValues.Length; i++)
        {
            float t = tValues[i];
            float u = 1.0f - t;
            outX[i] = u * u * p0x + 2.0f * u * t * p1x + t * t * p2x;
            outY[i] = u * u * p0y + 2.0f * u * t * p1y + t * t * p2y;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static void EvaluateQuadraticScalar(
        float p0x, float p0y, float p1x, float p1y, float p2x, float p2y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        for (int i = 0; i < tValues.Length; i++)
        {
            float t = tValues[i];
            float u = 1.0f - t;
            outX[i] = u * u * p0x + 2.0f * u * t * p1x + t * t * p2x;
            outY[i] = u * u * p0y + 2.0f * u * t * p1y + t * t * p2y;
        }
    }

    /// <summary>
    /// Evaluates cubic Bézier curve using the best available SIMD hardware (AVX-512, Vector256, or scalar).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EvaluateCubic(
        float p0x, float p0y, float p1x, float p1y,
        float p2x, float p2y, float p3x, float p3y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        if (Vector512.IsHardwareAccelerated && tValues.Length >= Vector512<float>.Count)
        {
            EvaluateCubicAvx512(p0x, p0y, p1x, p1y, p2x, p2y, p3x, p3y, tValues, outX, outY);
        }
        else if (Vector256.IsHardwareAccelerated && tValues.Length >= Vector256<float>.Count)
        {
            EvaluateCubicVector256(p0x, p0y, p1x, p1y, p2x, p2y, p3x, p3y, tValues, outX, outY);
        }
        else
        {
            EvaluateCubicScalar(p0x, p0y, p1x, p1y, p2x, p2y, p3x, p3y, tValues, outX, outY);
        }
    }

    /// <summary>
    /// Evaluates quadratic Bézier curve using the best available SIMD hardware (AVX-512, Vector256, or scalar).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EvaluateQuadratic(
        float p0x, float p0y, float p1x, float p1y, float p2x, float p2y,
        ReadOnlySpan<float> tValues, Span<float> outX, Span<float> outY)
    {
        if (Vector512.IsHardwareAccelerated && tValues.Length >= Vector512<float>.Count)
        {
            EvaluateQuadraticAvx512(p0x, p0y, p1x, p1y, p2x, p2y, tValues, outX, outY);
        }
        else if (Vector256.IsHardwareAccelerated && tValues.Length >= Vector256<float>.Count)
        {
            EvaluateQuadraticVector256(p0x, p0y, p1x, p1y, p2x, p2y, tValues, outX, outY);
        }
        else
        {
            EvaluateQuadraticScalar(p0x, p0y, p1x, p1y, p2x, p2y, tValues, outX, outY);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PointF EvaluateCubicPoint(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        float u = 1.0f - t;
        float c0 = u * u * u;
        float c1 = 3.0f * u * u * t;
        float c2 = 3.0f * u * t * t;
        float c3 = t * t * t;
        return new PointF(
            c0 * p0.X + c1 * p1.X + c2 * p2.X + c3 * p3.X,
            c0 * p0.Y + c1 * p1.Y + c2 * p2.Y + c3 * p3.Y);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PointF EvaluateQuadraticPoint(PointF p0, PointF p1, PointF p2, float t)
    {
        float u = 1.0f - t;
        float c0 = u * u;
        float c1 = 2.0f * u * t;
        float c2 = t * t;
        return new PointF(
            c0 * p0.X + c1 * p1.X + c2 * p2.X,
            c0 * p0.Y + c1 * p1.Y + c2 * p2.Y);
    }
}
