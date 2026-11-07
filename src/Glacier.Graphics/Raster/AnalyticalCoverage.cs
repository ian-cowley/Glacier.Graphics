namespace Glacier.Graphics.Raster;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Analytical trapezoidal coverage calculations for anti-aliased vector rendering.
/// Computes exact signed area between pixel boundaries and line segments, including left-of-origin backdrop winding.
/// </summary>
public static class AnalyticalCoverage
{
    /// <summary>
    /// Computes the signed trapezoidal area of a line segment within a unit pixel cell [0, 1] x [0, 1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeTrapezoidSignedArea(float x1, float y1, float x2, float y2)
    {
        return (y2 - y1) * (1.0f - (x1 + x2) * 0.5f);
    }

    /// <summary>
    /// Accumulates line segment coverage into a scanline delta buffer of length <paramref name="width"/> + 1.
    /// Segments or partial segments lying at <c>x &lt;= 0</c> contribute exact signed vertical span to the left backdrop
    /// entering column <c>0</c>.
    /// </summary>
    public static void AccumulateSegment(
        Span<float> coverageDeltas,
        Span<float> areaDeltas,
        int width,
        float x1, float y1, float x2, float y2)
    {
        if (width <= 0 || MathF.Abs(y1 - y2) < 1e-5f) return;

        float dir = 1f;
        if (y1 > y2)
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
            dir = -1f;
        }

        float minX = MathF.Min(x1, x2);
        float maxX = MathF.Max(x1, x2);

        // Entire segment is at or to the right of the viewport/tile right edge: zero contribution inside [0, width)
        if (minX >= width)
        {
            return;
        }

        // Entire segment is at or to the left of x = 0: pure left-backdrop winding for all columns [0, width)
        if (maxX <= 0f)
        {
            float fullDy = dir * (y2 - y1);
            areaDeltas[0] += fullDy;
            coverageDeltas[0] += fullDy;
            return;
        }

        // If the segment crosses x = 0, split off the portion with x <= 0 as exact left-backdrop winding
        if (minX < 0f)
        {
            float tZero = (0f - x1) / (x2 - x1);
            float yZero = y1 + tZero * (y2 - y1);
            if (x1 < 0f)
            {
                float leftDy = dir * (yZero - y1);
                areaDeltas[0] += leftDy;
                coverageDeltas[0] += leftDy;
                x1 = 0f;
                y1 = yZero;
            }
            else
            {
                float leftDy = dir * (y2 - yZero);
                areaDeltas[0] += leftDy;
                coverageDeltas[0] += leftDy;
                x2 = 0f;
                y2 = yZero;
            }

            if (MathF.Abs(y2 - y1) < 1e-6f)
            {
                return;
            }
        }

        // Clip portion beyond x = width (segments to the right of width do not affect columns < width)
        if (MathF.Max(x1, x2) > width)
        {
            float tRight = (width - x1) / (x2 - x1);
            float yRight = y1 + tRight * (y2 - y1);
            if (x2 > width)
            {
                x2 = width;
                y2 = yRight;
            }
            else
            {
                x1 = width;
                y1 = yRight;
            }

            if (MathF.Abs(y2 - y1) < 1e-6f)
            {
                return;
            }
        }

        int xStart = Math.Clamp((int)MathF.Floor(MathF.Min(x1, x2)), 0, width - 1);
        int xEnd = Math.Clamp((int)MathF.Floor(MathF.Min(MathF.Max(x1, x2), MathF.BitDecrement((float)width))), 0, width - 1);

        if (xStart == xEnd)
        {
            float u1 = Math.Clamp(x1 - xStart, 0f, 1f);
            float u2 = Math.Clamp(x2 - xStart, 0f, 1f);
            float dy = y2 - y1;

            areaDeltas[xStart] += dir * dy * (1.0f - (u1 + u2) * 0.5f);
            coverageDeltas[xStart] += dir * dy;
            return;
        }

        float dx = x2 - x1;
        float dyTotal = y2 - y1;

        for (int x = xStart; x <= xEnd; x++)
        {
            float xLeft = x;
            float xRight = x + 1.0f;

            float t0 = Math.Clamp((xLeft - x1) / dx, 0f, 1f);
            float t1 = Math.Clamp((xRight - x1) / dx, 0f, 1f);

            if (t0 > t1) (t0, t1) = (t1, t0);

            float segY0 = y1 + t0 * dyTotal;
            float segY1 = y1 + t1 * dyTotal;
            float segX0 = Math.Clamp(x1 + t0 * dx - x, 0f, 1f);
            float segX1 = Math.Clamp(x1 + t1 * dx - x, 0f, 1f);

            float dySeg = segY1 - segY0;
            if (MathF.Abs(dySeg) > 1e-6f)
            {
                areaDeltas[x] += dir * dySeg * (1.0f - (segX0 + segX1) * 0.5f);
                coverageDeltas[x] += dir * dySeg;
            }
        }
    }
}
