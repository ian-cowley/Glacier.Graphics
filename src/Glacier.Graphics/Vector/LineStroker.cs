namespace Glacier.Graphics.Vector;

using System;
using System.Collections.Generic;

/// <summary>
/// Analytical line stroker supporting Miter, Round, and Bevel joins,
/// Butt, Square, and Round caps, and pooled-scratch dashing.
/// </summary>
public static class LineStroker
{
    private sealed class ScratchBuffers
    {
        public readonly List<PointF> LeftOrOuter = new(64);
        public readonly List<PointF> RightOrInner = new(64);
        public readonly List<PointF> DashSegment = new(64);
    }

    [ThreadStatic]
    private static ScratchBuffers? t_scratch;

    private static ScratchBuffers GetScratch() => t_scratch ??= new ScratchBuffers();

    public static VectorPath Stroke(
        VectorPath path,
        float strokeWidth,
        StrokeCap cap = StrokeCap.Butt,
        StrokeJoin join = StrokeJoin.Miter,
        float miterLimit = 4.0f,
        ReadOnlySpan<float> dashPattern = default,
        float dashPhase = 0f)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        var result = new VectorPath();
        if (strokeWidth <= 0f) return result;

        float halfWidth = strokeWidth * 0.5f;
        var contours = path.Flatten(0.25f);
        var scratch = GetScratch();

        foreach (var contour in contours)
        {
            if (contour.Count < 2) continue;

            bool isClosed = (contour[0] == contour[^1]) && contour.Count > 2;

            if (dashPattern.Length > 0)
            {
                StrokeDashedContour(result, contour, dashPattern, dashPhase, halfWidth, cap, join, miterLimit, scratch);
            }
            else
            {
                if (isClosed)
                {
                    StrokeClosedContour(result, contour, halfWidth, join, miterLimit, scratch);
                }
                else
                {
                    StrokeOpenContour(result, contour, halfWidth, cap, join, miterLimit, scratch);
                }
            }
        }

        return result;
    }

    private static void StrokeOpenContour(
        VectorPath outPath,
        List<PointF> pts,
        float halfWidth,
        StrokeCap cap,
        StrokeJoin join,
        float miterLimit,
        ScratchBuffers scratch)
    {
        int n = pts.Count;
        if (n < 2) return;

        var left = scratch.LeftOrOuter;
        var right = scratch.RightOrInner;
        left.Clear();
        right.Clear();

        PointF d0 = (pts[1] - pts[0]).Normalize();
        PointF n0 = new PointF(-d0.Y, d0.X);

        left.Add(pts[0] + n0 * halfWidth);
        right.Add(pts[0] - n0 * halfWidth);

        for (int i = 1; i < n - 1; i++)
        {
            PointF pPrev = pts[i - 1];
            PointF pCurr = pts[i];
            PointF pNext = pts[i + 1];

            PointF d1 = (pCurr - pPrev).Normalize();
            PointF d2 = (pNext - pCurr).Normalize();
            PointF n1 = new PointF(-d1.Y, d1.X);
            PointF n2 = new PointF(-d2.Y, d2.X);

            AddJoin(left, right, pCurr, n1, n2, d1, d2, halfWidth, join, miterLimit);
        }

        PointF dLast = (pts[n - 1] - pts[n - 2]).Normalize();
        PointF nLast = new PointF(-dLast.Y, dLast.X);

        left.Add(pts[n - 1] + nLast * halfWidth);
        right.Add(pts[n - 1] - nLast * halfWidth);

        outPath.MoveTo(left[0]);
        for (int i = 1; i < left.Count; i++)
        {
            outPath.LineTo(left[i]);
        }

        AddCap(outPath, pts[n - 1], left[^1], right[^1], dLast, halfWidth, cap);

        for (int i = right.Count - 1; i >= 0; i--)
        {
            outPath.LineTo(right[i]);
        }

        AddCap(outPath, pts[0], right[0], left[0], PointF.Zero - d0, halfWidth, cap);

        outPath.Close();
    }

    private static void StrokeClosedContour(
        VectorPath outPath,
        List<PointF> pts,
        float halfWidth,
        StrokeJoin join,
        float miterLimit,
        ScratchBuffers scratch)
    {
        int n = pts.Count - 1; // last point equals first
        if (n < 3) return;

        var outer = scratch.LeftOrOuter;
        var inner = scratch.RightOrInner;
        outer.Clear();
        inner.Clear();

        for (int i = 0; i < n; i++)
        {
            PointF pPrev = pts[(i - 1 + n) % n];
            PointF pCurr = pts[i];
            PointF pNext = pts[(i + 1) % n];

            PointF d1 = (pCurr - pPrev).Normalize();
            PointF d2 = (pNext - pCurr).Normalize();
            PointF n1 = new PointF(-d1.Y, d1.X);
            PointF n2 = new PointF(-d2.Y, d2.X);

            AddJoin(outer, inner, pCurr, n1, n2, d1, d2, halfWidth, join, miterLimit);
        }

        if (outer.Count > 0)
        {
            outPath.MoveTo(outer[0]);
            for (int i = 1; i < outer.Count; i++) outPath.LineTo(outer[i]);
            outPath.Close();
        }

        if (inner.Count > 0)
        {
            // Emit inner contour in reverse so its winding opposes the outer contour.
            outPath.MoveTo(inner[inner.Count - 1]);
            for (int i = inner.Count - 2; i >= 0; i--) outPath.LineTo(inner[i]);
            outPath.Close();
        }
    }

    private static void AddJoin(
        List<PointF> left,
        List<PointF> right,
        PointF p,
        PointF n1,
        PointF n2,
        PointF d1,
        PointF d2,
        float halfWidth,
        StrokeJoin join,
        float miterLimit)
    {
        float cross = d1.X * d2.Y - d1.Y * d2.X;
        float dot = n1.X * n2.X + n1.Y * n2.Y;

        if (MathF.Abs(cross) < 1e-4f)
        {
            left.Add(p + n1 * halfWidth);
            right.Add(p - n1 * halfWidth);
            return;
        }

        bool turnsLeft = cross > 0f;

        if (join == StrokeJoin.Miter)
        {
            float denom = 1.0f + dot;
            if (denom > 1e-4f)
            {
                float miterLength = halfWidth * MathF.Sqrt(2f / denom);
                if (miterLength <= halfWidth * miterLimit)
                {
                    PointF miterVec = (n1 + n2) * (halfWidth / denom);
                    if (turnsLeft)
                    {
                        left.Add(p + miterVec);
                        right.Add(p - n1 * halfWidth);
                        right.Add(p - n2 * halfWidth);
                    }
                    else
                    {
                        left.Add(p + n1 * halfWidth);
                        left.Add(p + n2 * halfWidth);
                        right.Add(p - miterVec);
                    }
                    return;
                }
            }
            join = StrokeJoin.Bevel;
        }

        if (join == StrokeJoin.Round)
        {
            if (turnsLeft)
            {
                right.Add(p - n1 * halfWidth);
                right.Add(p - n2 * halfWidth);

                int steps = Math.Clamp((int)(halfWidth * 0.5f) + 4, 4, 16);
                for (int s = 0; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    PointF nInterp = (n1 * (1f - t) + n2 * t).Normalize();
                    left.Add(p + nInterp * halfWidth);
                }
            }
            else
            {
                left.Add(p + n1 * halfWidth);
                left.Add(p + n2 * halfWidth);

                int steps = Math.Clamp((int)(halfWidth * 0.5f) + 4, 4, 16);
                for (int s = 0; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    PointF nInterp = (n1 * (1f - t) + n2 * t).Normalize();
                    right.Add(p - nInterp * halfWidth);
                }
            }
        }
        else // Bevel
        {
            left.Add(p + n1 * halfWidth);
            left.Add(p + n2 * halfWidth);
            right.Add(p - n1 * halfWidth);
            right.Add(p - n2 * halfWidth);
        }
    }

    private static void AddCap(
        VectorPath outPath,
        PointF anchor,
        PointF pFrom,
        PointF pTo,
        PointF forwardDir,
        float halfWidth,
        StrokeCap cap)
    {
        switch (cap)
        {
            case StrokeCap.Butt:
                outPath.LineTo(pTo);
                break;

            case StrokeCap.Square:
                PointF ext = forwardDir * halfWidth;
                outPath.LineTo(pFrom + ext);
                outPath.LineTo(pTo + ext);
                outPath.LineTo(pTo);
                break;

            case StrokeCap.Round:
                int steps = 8;
                PointF normal = (pTo - pFrom).Normalize();
                for (int s = 1; s < steps; s++)
                {
                    float angle = MathF.PI * ((float)s / steps);
                    float sin = MathF.Sin(angle);
                    float cos = MathF.Cos(angle);
                    PointF pt = anchor + (normal * (-cos) + forwardDir * sin) * halfWidth;
                    outPath.LineTo(pt);
                }
                outPath.LineTo(pTo);
                break;
        }
    }

    private static void StrokeDashedContour(
        VectorPath outPath,
        List<PointF> contour,
        ReadOnlySpan<float> dashPattern,
        float dashPhase,
        float halfWidth,
        StrokeCap cap,
        StrokeJoin join,
        float miterLimit,
        ScratchBuffers scratch)
    {
        if (contour.Count < 2 || dashPattern.Length == 0) return;

        float patternLen = 0f;
        for (int i = 0; i < dashPattern.Length; i++)
            patternLen += dashPattern[i];

        if (patternLen <= 0f) return;

        float phase = dashPhase % patternLen;
        if (phase < 0f) phase += patternLen;

        int dashIdx = 0;
        float dashRem = 0f;
        float accum = 0f;
        for (int i = 0; i < dashPattern.Length; i++)
        {
            if (accum + dashPattern[i] > phase)
            {
                dashIdx = i;
                dashRem = (accum + dashPattern[i]) - phase;
                break;
            }
            accum += dashPattern[i];
        }

        var currentDash = scratch.DashSegment;
        currentDash.Clear();

        bool isDraw = (dashIdx % 2 == 0);
        if (isDraw)
        {
            currentDash.Add(contour[0]);
        }

        PointF prev = contour[0];
        for (int i = 1; i < contour.Count; i++)
        {
            PointF curr = contour[i];
            float segLen = (curr - prev).Length();
            if (segLen < 1e-5f) continue;

            PointF dir = (curr - prev) / segLen;
            float segDist = 0f;

            while (segDist < segLen)
            {
                float needed = dashRem;
                float available = segLen - segDist;

                if (available < needed)
                {
                    segDist = segLen;
                    dashRem -= available;
                    if (isDraw)
                    {
                        currentDash.Add(curr);
                    }
                }
                else
                {
                    segDist += needed;
                    PointF splitPt = prev + dir * segDist;

                    if (isDraw)
                    {
                        currentDash.Add(splitPt);
                        if (currentDash.Count >= 2)
                        {
                            StrokeOpenContour(outPath, currentDash, halfWidth, cap, join, miterLimit, scratch);
                        }
                        currentDash.Clear();
                    }

                    dashIdx = (dashIdx + 1) % dashPattern.Length;
                    dashRem = dashPattern[dashIdx];
                    isDraw = (dashIdx % 2 == 0);

                    if (isDraw)
                    {
                        currentDash.Add(splitPt);
                    }
                }
            }

            prev = curr;
        }

        if (isDraw && currentDash.Count >= 2)
        {
            StrokeOpenContour(outPath, currentDash, halfWidth, cap, join, miterLimit, scratch);
        }
        currentDash.Clear();
    }
}
