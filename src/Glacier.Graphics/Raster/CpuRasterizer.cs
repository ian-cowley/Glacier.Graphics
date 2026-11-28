namespace Glacier.Graphics.Raster;

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Glacier.Graphics.Vector;

/// <summary>
/// Multi-threaded CPU SIMD rasterizer utilizing AVX-512 / Vector256 scanline prefix-sum
/// and vectorized straight-alpha Porter-Duff SRC_OVER span blending with analytical anti-aliasing.
/// </summary>
public sealed class CpuRasterizer
{
    private const int BandHeight = 32;

    // Vector256 inclusive-scan shuffle indices and masks
    private static readonly Vector256<int> ScanIdx1_256 = Vector256.Create(0, 0, 1, 2, 3, 4, 5, 6);
    private static readonly Vector256<int> ScanIdx2_256 = Vector256.Create(0, 0, 0, 1, 2, 3, 4, 5);
    private static readonly Vector256<int> ScanIdx4_256 = Vector256.Create(0, 0, 0, 0, 0, 1, 2, 3);
    private static readonly Vector256<float> ScanMask1_256 = Vector256.Create(0, -1, -1, -1, -1, -1, -1, -1).AsSingle();
    private static readonly Vector256<float> ScanMask2_256 = Vector256.Create(0, 0, -1, -1, -1, -1, -1, -1).AsSingle();
    private static readonly Vector256<float> ScanMask4_256 = Vector256.Create(0, 0, 0, 0, -1, -1, -1, -1).AsSingle();

    // Vector512 inclusive-scan shuffle indices and masks
    private static readonly Vector512<int> ScanIdx1_512 = Vector512.Create(0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14);
    private static readonly Vector512<int> ScanIdx2_512 = Vector512.Create(0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13);
    private static readonly Vector512<int> ScanIdx4_512 = Vector512.Create(0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
    private static readonly Vector512<int> ScanIdx8_512 = Vector512.Create(0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7);
    private static readonly Vector512<float> ScanMask1_512 = Vector512.Create(0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1).AsSingle();
    private static readonly Vector512<float> ScanMask2_512 = Vector512.Create(0, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1).AsSingle();
    private static readonly Vector512<float> ScanMask4_512 = Vector512.Create(0, 0, 0, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1).AsSingle();
    private static readonly Vector512<float> ScanMask8_512 = Vector512.Create(0, 0, 0, 0, 0, 0, 0, 0, -1, -1, -1, -1, -1, -1, -1, -1).AsSingle();

    public void FillPath(
        LinearFramebuffer framebuffer,
        VectorPath path,
        Rgba32 color,
        WindingRule rule = WindingRule.NonZero,
        RectF clipRect = default)
    {
        RectF? explicitClip = clipRect == default ? null : clipRect;
        FillPath(framebuffer, path, color, rule, explicitClip);
    }

    public void FillPath(
        LinearFramebuffer framebuffer,
        VectorPath path,
        Rgba32 color,
        WindingRule rule,
        RectF? clipRect)
    {
        if (framebuffer == null || path == null || path.PointCount < 3 || color.A == 0) return;

        RectF effectiveClip = clipRect ?? RectF.FromLTRB(0, 0, framebuffer.Width, framebuffer.Height);
        if (!(effectiveClip.Width > 0f && effectiveClip.Height > 0f)) return;

        int width = framebuffer.Width;
        int height = framebuffer.Height;
        int minX = Math.Max(0, (int)MathF.Floor(effectiveClip.Left));
        int maxX = Math.Min(width, (int)MathF.Ceiling(effectiveClip.Right));
        int clipMinY = Math.Max(0, (int)MathF.Floor(effectiveClip.Top));
        int clipMaxY = Math.Min(height, (int)MathF.Ceiling(effectiveClip.Bottom));
        if (minX >= maxX || clipMinY >= clipMaxY) return;

        var contours = path.Flatten(0.25f);
        if (contours.Count == 0) return;

        int numBands = (height + BandHeight - 1) / BandHeight;

        Parallel.For(0, numBands, bandIdx =>
        {
            int startY = Math.Max(clipMinY, bandIdx * BandHeight);
            int endY = Math.Min(clipMaxY, (bandIdx + 1) * BandHeight);
            if (startY >= endY) return;

            float[]? rentedCov = null;
            float[]? rentedArea = null;
            Span<float> coverageDeltas = width <= 2048
                ? stackalloc float[width + 1]
                : (rentedCov = ArrayPool<float>.Shared.Rent(width + 1)).AsSpan(0, width + 1);
            Span<float> areaDeltas = width <= 2048
                ? stackalloc float[width + 1]
                : (rentedArea = ArrayPool<float>.Shared.Rent(width + 1)).AsSpan(0, width + 1);

            try
            {
                for (int y = startY; y < endY; y++)
                {
                    coverageDeltas.Clear();
                    areaDeltas.Clear();

                    float lineTop = y;
                    float lineBottom = y + 1.0f;

                    foreach (var contour in contours)
                    {
                        int n = contour.Count;
                        for (int i = 0; i < n; i++)
                        {
                            PointF p0 = contour[i];
                            PointF p1 = contour[(i + 1) % n];

                            float minY = MathF.Min(p0.Y, p1.Y);
                            float maxY = MathF.Max(p0.Y, p1.Y);
                            if (maxY <= lineTop || minY >= lineBottom) continue;

                            float clampedY0 = Math.Clamp(p0.Y, lineTop, lineBottom);
                            float clampedY1 = Math.Clamp(p1.Y, lineTop, lineBottom);
                            if (MathF.Abs(clampedY0 - clampedY1) < 1e-5f) continue;

                            float dy = p1.Y - p0.Y;
                            float t0 = (clampedY0 - p0.Y) / dy;
                            float t1 = (clampedY1 - p0.Y) / dy;
                            float clampedX0 = p0.X + t0 * (p1.X - p0.X);
                            float clampedX1 = p0.X + t1 * (p1.X - p0.X);

                            AnalyticalCoverage.AccumulateSegment(
                                coverageDeltas, areaDeltas, width,
                                clampedX0, clampedY0 - lineTop, clampedX1, clampedY1 - lineTop);
                        }
                    }

                    var rowSpan = framebuffer.GetRowSpan(y);
                    ProcessScanline(coverageDeltas, areaDeltas, rowSpan, minX, maxX, color, rule);
                }
            }
            finally
            {
                if (rentedCov is not null) ArrayPool<float>.Shared.Return(rentedCov);
                if (rentedArea is not null) ArrayPool<float>.Shared.Return(rentedArea);
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ProcessScanline(
        ReadOnlySpan<float> coverageDeltas,
        ReadOnlySpan<float> areaDeltas,
        Span<Rgba32> rowSpan,
        int minX,
        int maxX,
        Rgba32 color,
        WindingRule rule)
    {
        float accumCoverage = 0f;
        int x = 0;

        // Accumulate coverage prefix before clip minX
        for (; x < minX; x++)
        {
            accumCoverage += coverageDeltas[x];
        }

        Span<uint> dstUint = MemoryMarshal.Cast<Rgba32, uint>(rowSpan);
        uint colorUint = color.ToRgbaUint();

        if (Vector512.IsHardwareAccelerated && maxX - x >= Vector512<float>.Count)
        {
            const int step = 16; // Vector512<float>.Count
            var vZero = Vector512<float>.Zero;
            var vOne = Vector512.Create(1.0f);
            var vHalf = Vector512.Create(0.5f);
            var vTwo = Vector512.Create(2.0f);
            var vThresh = Vector512.Create(0.001f);
            var v255 = Vector512.Create(255.0f);
            var vInv255 = Vector512.Create(1.0f / 255.0f);
            var vEps = Vector512.Create(1e-6f);
            var vSrcR = Vector512.Create((float)color.R);
            var vSrcG = Vector512.Create((float)color.G);
            var vSrcB = Vector512.Create((float)color.B);
            var vSrcA = Vector512.Create(color.A / 255.0f);
            var vMaskFF = Vector512.Create(0xFFu);
            var vAlphaMask = Vector512.Create(0xFF000000u);
            var vColorVec = Vector512.Create(colorUint);

            for (; x <= maxX - step; x += step)
            {
                var vCov = Vector512.Create(coverageDeltas.Slice(x, step));
                var vArea = Vector512.Create(areaDeltas.Slice(x, step));

                // Inclusive prefix sum across 16 lanes
                var vInc = vCov;
                vInc += Vector512.Shuffle(vInc, ScanIdx1_512) & ScanMask1_512;
                vInc += Vector512.Shuffle(vInc, ScanIdx2_512) & ScanMask2_512;
                vInc += Vector512.Shuffle(vInc, ScanIdx4_512) & ScanMask4_512;
                vInc += Vector512.Shuffle(vInc, ScanIdx8_512) & ScanMask8_512;

                var vExc = (Vector512.Shuffle(vInc, ScanIdx1_512) & ScanMask1_512) + Vector512.Create(accumCoverage);
                accumCoverage += vInc.GetElement(15);

                var vPixelArea = vExc + vArea;
                Vector512<float> vRawAlpha = rule == WindingRule.EvenOdd
                    ? Vector512.Abs(vPixelArea - vTwo * Vector512.Round(vPixelArea * vHalf))
                    : Vector512.Abs(vPixelArea);
                var vAlpha = Vector512.Min(vOne, Vector512.Max(vZero, vRawAlpha));

                if (Vector512.LessThanOrEqualAll(vAlpha, vThresh))
                {
                    continue;
                }

                if (color.A == 255 && Vector512.GreaterThanOrEqualAll(vAlpha, vOne))
                {
                    vColorVec.CopyTo(dstUint.Slice(x, step));
                    continue;
                }

                var vDst = Vector512.Create(dstUint.Slice(x, step));
                var vSa = Vector512.Min(vOne, vSrcA * vAlpha);
                var vDstR = Vector512.ConvertToSingle((vDst & vMaskFF).AsInt32());
                var vDstG = Vector512.ConvertToSingle(((vDst >>> 8) & vMaskFF).AsInt32());
                var vDstB = Vector512.ConvertToSingle(((vDst >>> 16) & vMaskFF).AsInt32());

                Vector512<uint> vBlended;
                if (Vector512.EqualsAll(vDst & vAlphaMask, vAlphaMask))
                {
                    var vInvA = vOne - vSa;
                    var vOutR = Vector512.Min(v255, Vector512.Max(vZero, vSrcR * vSa + vDstR * vInvA));
                    var vOutG = Vector512.Min(v255, Vector512.Max(vZero, vSrcG * vSa + vDstG * vInvA));
                    var vOutB = Vector512.Min(v255, Vector512.Max(vZero, vSrcB * vSa + vDstB * vInvA));

                    var ur = Vector512.ConvertToInt32(vOutR).AsUInt32();
                    var ug = Vector512.ConvertToInt32(vOutG).AsUInt32();
                    var ub = Vector512.ConvertToInt32(vOutB).AsUInt32();
                    vBlended = ur | (ug << 8) | (ub << 16) | vAlphaMask;
                }
                else
                {
                    var vDstA = Vector512.ConvertToSingle((vDst >>> 24).AsInt32());
                    var vDa = vDstA * vInv255;
                    var vDstFactor = vDa * (vOne - vSa);
                    var vOutA = vSa + vDstFactor;
                    var vInvOutA = vOne / Vector512.Max(vOutA, vEps);

                    var vOutR = Vector512.Min(v255, Vector512.Max(vZero, (vSrcR * vSa + vDstR * vDstFactor) * vInvOutA));
                    var vOutG = Vector512.Min(v255, Vector512.Max(vZero, (vSrcG * vSa + vDstG * vDstFactor) * vInvOutA));
                    var vOutB = Vector512.Min(v255, Vector512.Max(vZero, (vSrcB * vSa + vDstB * vDstFactor) * vInvOutA));
                    var vOutByteA = Vector512.Min(v255, Vector512.Max(vZero, vOutA * v255));

                    var ur = Vector512.ConvertToInt32(vOutR).AsUInt32();
                    var ug = Vector512.ConvertToInt32(vOutG).AsUInt32();
                    var ub = Vector512.ConvertToInt32(vOutB).AsUInt32();
                    var ua = Vector512.ConvertToInt32(vOutByteA).AsUInt32();
                    var packed = ur | (ug << 8) | (ub << 16) | (ua << 24);
                    vBlended = Vector512.ConditionalSelect(Vector512.GreaterThan(vOutA, vZero).AsUInt32(), packed, Vector512<uint>.Zero);
                }

                var activeMask = Vector512.GreaterThan(vAlpha, vThresh).AsUInt32();
                var vFinal = Vector512.ConditionalSelect(activeMask, vBlended, vDst);
                vFinal.CopyTo(dstUint.Slice(x, step));
            }
        }
        else if (Vector256.IsHardwareAccelerated && maxX - x >= Vector256<float>.Count)
        {
            const int step = 8; // Vector256<float>.Count
            var vZero = Vector256<float>.Zero;
            var vOne = Vector256.Create(1.0f);
            var vHalf = Vector256.Create(0.5f);
            var vTwo = Vector256.Create(2.0f);
            var vThresh = Vector256.Create(0.001f);
            var v255 = Vector256.Create(255.0f);
            var vInv255 = Vector256.Create(1.0f / 255.0f);
            var vEps = Vector256.Create(1e-6f);
            var vSrcR = Vector256.Create((float)color.R);
            var vSrcG = Vector256.Create((float)color.G);
            var vSrcB = Vector256.Create((float)color.B);
            var vSrcA = Vector256.Create(color.A / 255.0f);
            var vMaskFF = Vector256.Create(0xFFu);
            var vAlphaMask = Vector256.Create(0xFF000000u);
            var vColorVec = Vector256.Create(colorUint);

            for (; x <= maxX - step; x += step)
            {
                var vCov = Vector256.Create(coverageDeltas.Slice(x, step));
                var vArea = Vector256.Create(areaDeltas.Slice(x, step));

                // Inclusive prefix sum across 8 lanes
                var vInc = vCov;
                vInc += Vector256.Shuffle(vInc, ScanIdx1_256) & ScanMask1_256;
                vInc += Vector256.Shuffle(vInc, ScanIdx2_256) & ScanMask2_256;
                vInc += Vector256.Shuffle(vInc, ScanIdx4_256) & ScanMask4_256;

                var vExc = (Vector256.Shuffle(vInc, ScanIdx1_256) & ScanMask1_256) + Vector256.Create(accumCoverage);
                accumCoverage += vInc.GetElement(7);

                var vPixelArea = vExc + vArea;
                Vector256<float> vRawAlpha = rule == WindingRule.EvenOdd
                    ? Vector256.Abs(vPixelArea - vTwo * Vector256.Round(vPixelArea * vHalf))
                    : Vector256.Abs(vPixelArea);
                var vAlpha = Vector256.Min(vOne, Vector256.Max(vZero, vRawAlpha));

                if (Vector256.LessThanOrEqualAll(vAlpha, vThresh))
                {
                    continue;
                }

                if (color.A == 255 && Vector256.GreaterThanOrEqualAll(vAlpha, vOne))
                {
                    vColorVec.CopyTo(dstUint.Slice(x, step));
                    continue;
                }

                var vDst = Vector256.Create(dstUint.Slice(x, step));
                var vSa = Vector256.Min(vOne, vSrcA * vAlpha);
                var vDstR = Vector256.ConvertToSingle((vDst & vMaskFF).AsInt32());
                var vDstG = Vector256.ConvertToSingle(((vDst >>> 8) & vMaskFF).AsInt32());
                var vDstB = Vector256.ConvertToSingle(((vDst >>> 16) & vMaskFF).AsInt32());

                Vector256<uint> vBlended;
                if (Vector256.EqualsAll(vDst & vAlphaMask, vAlphaMask))
                {
                    var vInvA = vOne - vSa;
                    var vOutR = Vector256.Min(v255, Vector256.Max(vZero, vSrcR * vSa + vDstR * vInvA));
                    var vOutG = Vector256.Min(v255, Vector256.Max(vZero, vSrcG * vSa + vDstG * vInvA));
                    var vOutB = Vector256.Min(v255, Vector256.Max(vZero, vSrcB * vSa + vDstB * vInvA));

                    var ur = Vector256.ConvertToInt32(vOutR).AsUInt32();
                    var ug = Vector256.ConvertToInt32(vOutG).AsUInt32();
                    var ub = Vector256.ConvertToInt32(vOutB).AsUInt32();
                    vBlended = ur | (ug << 8) | (ub << 16) | vAlphaMask;
                }
                else
                {
                    var vDstA = Vector256.ConvertToSingle((vDst >>> 24).AsInt32());
                    var vDa = vDstA * vInv255;
                    var vDstFactor = vDa * (vOne - vSa);
                    var vOutA = vSa + vDstFactor;
                    var vInvOutA = vOne / Vector256.Max(vOutA, vEps);

                    var vOutR = Vector256.Min(v255, Vector256.Max(vZero, (vSrcR * vSa + vDstR * vDstFactor) * vInvOutA));
                    var vOutG = Vector256.Min(v255, Vector256.Max(vZero, (vSrcG * vSa + vDstG * vDstFactor) * vInvOutA));
                    var vOutB = Vector256.Min(v255, Vector256.Max(vZero, (vSrcB * vSa + vDstB * vDstFactor) * vInvOutA));
                    var vOutByteA = Vector256.Min(v255, Vector256.Max(vZero, vOutA * v255));

                    var ur = Vector256.ConvertToInt32(vOutR).AsUInt32();
                    var ug = Vector256.ConvertToInt32(vOutG).AsUInt32();
                    var ub = Vector256.ConvertToInt32(vOutB).AsUInt32();
                    var ua = Vector256.ConvertToInt32(vOutByteA).AsUInt32();
                    var packed = ur | (ug << 8) | (ub << 16) | (ua << 24);
                    vBlended = Vector256.ConditionalSelect(Vector256.GreaterThan(vOutA, vZero).AsUInt32(), packed, Vector256<uint>.Zero);
                }

                var activeMask = Vector256.GreaterThan(vAlpha, vThresh).AsUInt32();
                var vFinal = Vector256.ConditionalSelect(activeMask, vBlended, vDst);
                vFinal.CopyTo(dstUint.Slice(x, step));
            }
        }

        // Scalar tail / fallback
        for (; x < maxX; x++)
        {
            float pixelArea = accumCoverage + areaDeltas[x];
            accumCoverage += coverageDeltas[x];

            float alpha = rule switch
            {
                WindingRule.NonZero => Math.Clamp(MathF.Abs(pixelArea), 0f, 1f),
                WindingRule.EvenOdd => Math.Clamp(MathF.Abs(pixelArea - 2.0f * MathF.Round(pixelArea * 0.5f)), 0f, 1f),
                _ => Math.Clamp(MathF.Abs(pixelArea), 0f, 1f)
            };

            if (alpha > 0.001f)
            {
                ColorBlend.BlendPixel(ref rowSpan[x], color, alpha);
            }
        }
    }

    public void StrokePath(
        LinearFramebuffer framebuffer,
        VectorPath path,
        in Paint paint,
        RectF clipRect = default)
    {
        RectF? explicitClip = clipRect == default ? null : clipRect;
        StrokePath(framebuffer, path, in paint, explicitClip);
    }

    public void StrokePath(
        LinearFramebuffer framebuffer,
        VectorPath path,
        in Paint paint,
        RectF? clipRect)
    {
        if (clipRect is { } rect && !(rect.Width > 0f && rect.Height > 0f)) return;

        VectorPath stroked = LineStroker.Stroke(
            path,
            paint.StrokeWidth,
            paint.Cap,
            paint.Join,
            paint.MiterLimit);

        FillPath(framebuffer, stroked, paint.Color, WindingRule.NonZero, clipRect);
    }
}
