namespace Glacier.Graphics.Raster;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Analytical trapezoidal coverage calculations for anti-aliased vector rendering.
/// Computes exact signed area between pixel boundaries and line segments.
/// </summary>
public static class AnalyticalCoverage
{
    /// <summary>
    /// Computes the signed trapezoidal area of a line segment within a unit pixel cell [0, 1] x [0, 1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeTrapezoidSignedArea(float x1, float y1, float x2, float y2)
    {
        // Height = (y2 - y1)
        // Average distance from right edge = 1.0 - (x1 + x2) / 2
        return (y2 - y1) * (1.0f - (x1 + x2) * 0.5f);
    }

    /// <summary>
    /// Accumulates line segment coverage into a scanline delta buffer.
    /// Uses cell clipping for segments spanning multiple pixel columns.
    /// </summary>
    public static void AccumulateSegment(
        Span<float> coverageDeltas,
        Span<float> areaDeltas,
        int width,
        float x1, float y1, float x2, float y2)
    {
        if (MathF.Abs(y1 - y2) < 1e-5f) return; // Horizontal segment contributes zero area

        // Ensure y1 < y2 for upward integration (or retain sign)
        float dir = 1f;
        if (y1 > y2)
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
            dir = -1f;
        }

        int xStart = (int)MathF.Floor(MathF.Min(x1, x2));
        int xEnd = (int)MathF.Floor(MathF.Max(x1, x2));

        if (xStart == xEnd)
        {
            // Segment lies entirely within one pixel column
            int x = Math.Clamp(xStart, 0, width - 1);
            float u1 = Math.Clamp(x1 - xStart, 0f, 1f);
            float u2 = Math.Clamp(x2 - xStart, 0f, 1f);
            float dy = y2 - y1;

            areaDeltas[x] += dir * dy * (1.0f - (u1 + u2) * 0.5f);
            coverageDeltas[x] += dir * dy;
            return;
        }

        // Multi-column segment: trace through pixel boundaries
        float dx = x2 - x1;
        float dyTotal = y2 - y1;

        for (int x = xStart; x <= xEnd; x++)
        {
            if (x < 0 || x >= width) continue;

            float xLeft = x;
            float xRight = x + 1.0f;

            float t0 = Math.Clamp((xLeft - x1) / dx, 0f, 1f);
            float t1 = Math.Clamp((xRight - x1) / dx, 0f, 1f);

            if (t0 > t1) (t0, t1) = (t1, t0);

            float segY0 = y1 + t0 * dyTotal;
            float segY1 = y1 + t1 * dyTotal;
            float segX0 = x1 + t0 * dx - x;
            float segX1 = x1 + t1 * dx - x;

            float dySeg = segY1 - segY0;
            if (MathF.Abs(dySeg) > 1e-6f)
            {
                areaDeltas[x] += dir * dySeg * (1.0f - (segX0 + segX1) * 0.5f);
                coverageDeltas[x] += dir * dySeg;
            }
        }
    }
}
