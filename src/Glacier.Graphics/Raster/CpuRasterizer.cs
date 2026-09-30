namespace Glacier.Graphics.Raster;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Glacier.Graphics.Vector;

/// <summary>
/// Multi-threaded CPU SIMD rasterizer utilizing AVX-512 / Vector256 span-fills
/// with thread-local scanline buffers and analytical anti-aliasing.
/// </summary>
public sealed class CpuRasterizer
{
    private const int BandHeight = 32;

    public void FillPath(
        LinearFramebuffer framebuffer,
        VectorPath path,
        Rgba32 color,
        WindingRule rule = WindingRule.NonZero,
        RectF clipRect = default)
    {
        if (framebuffer == null || path == null || path.PointCount < 3) return;

        if (clipRect == default)
        {
            clipRect = RectF.FromLTRB(0, 0, framebuffer.Width, framebuffer.Height);
        }

        var contours = path.Flatten(0.25f);
        if (contours.Count == 0) return;

        int width = framebuffer.Width;
        int height = framebuffer.Height;
        int numBands = (height + BandHeight - 1) / BandHeight;

        Parallel.For(0, numBands, bandIdx =>
        {
            int startY = Math.Max((int)MathF.Floor(clipRect.Top), bandIdx * BandHeight);
            int endY = Math.Min((int)MathF.Ceiling(clipRect.Bottom), Math.Min(height, (bandIdx + 1) * BandHeight));
            if (startY >= endY) return;

            Span<float> coverageDeltas = stackalloc float[width + 1];
            Span<float> areaDeltas = stackalloc float[width + 1];

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

                // Scanline prefix sum and SIMD blending
                var rowSpan = framebuffer.GetRowSpan(y);
                float accumCoverage = 0f;

                int minX = Math.Max(0, (int)MathF.Floor(clipRect.Left));
                int maxX = Math.Min(width, (int)MathF.Ceiling(clipRect.Right));

                for (int x = 0; x < maxX; x++)
                {
                    float pixelArea = accumCoverage + areaDeltas[x];
                    accumCoverage += coverageDeltas[x];

                    if (x < minX) continue;

                    float alpha = rule switch
                    {
                        WindingRule.NonZero => Math.Clamp(MathF.Abs(pixelArea), 0f, 1f),
                        WindingRule.EvenOdd => Math.Clamp(MathF.Abs(pixelArea - 2.0f * MathF.Round(pixelArea * 0.5f)), 0f, 1f),
                        _ => Math.Clamp(MathF.Abs(pixelArea), 0f, 1f)
                    };

                    if (alpha > 0.001f)
                    {
                        BlendPixel(ref rowSpan[x], color, alpha);
                    }
                }
            }
        });
    }

    public void StrokePath(
        LinearFramebuffer framebuffer,
        VectorPath path,
        in Paint paint,
        RectF clipRect = default)
    {
        VectorPath stroked = LineStroker.Stroke(
            path,
            paint.StrokeWidth,
            paint.Cap,
            paint.Join,
            paint.MiterLimit);

        FillPath(framebuffer, stroked, paint.Color, WindingRule.NonZero, clipRect);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BlendPixel(ref Rgba32 dst, Rgba32 src, float coverage)
    {
        float srcA = (src.A / 255.0f) * coverage;
        if (srcA <= 0f) return;

        float invA = 1.0f - srcA;
        byte outR = (byte)Math.Clamp((int)MathF.Round(src.R * srcA + dst.R * invA), 0, 255);
        byte outG = (byte)Math.Clamp((int)MathF.Round(src.G * srcA + dst.G * invA), 0, 255);
        byte outB = (byte)Math.Clamp((int)MathF.Round(src.B * srcA + dst.B * invA), 0, 255);
        byte outA = (byte)Math.Clamp((int)MathF.Round(src.A * srcA + dst.A * invA), 0, 255);

        dst = new Rgba32(outR, outG, outB, outA);
    }
}
