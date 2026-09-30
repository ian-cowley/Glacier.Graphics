namespace Glacier.Graphics.Vector;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// High-throughput zero/low-allocation polygon clipper implementing Sutherland-Hodgman
/// and Vatti general clipping algorithms backed by native unmanaged scratch buffers.
/// </summary>
public static unsafe class PolygonClipper
{
    /// <summary>
    /// Clips a polygon against an axis-aligned bounding rectangle using Sutherland-Hodgman.
    /// Uses unmanaged native memory scratch buffers to avoid managed GC allocations.
    /// </summary>
    public static int ClipToRect(ReadOnlySpan<PointF> input, in RectF rect, Span<PointF> output)
    {
        if (input.Length < 3) return 0;

        int maxVertices = Math.Max(input.Length * 4, 64);
        PointF* bufferA = (PointF*)NativeMemory.Alloc((nuint)(maxVertices * sizeof(PointF)));
        PointF* bufferB = (PointF*)NativeMemory.Alloc((nuint)(maxVertices * sizeof(PointF)));

        try
        {
            fixed (PointF* pInput = input)
            {
                Buffer.MemoryCopy(pInput, bufferA, maxVertices * sizeof(PointF), input.Length * sizeof(PointF));
            }

            int count = input.Length;

            count = ClipLeft(bufferA, count, bufferB, maxVertices, rect.Left);
            count = ClipRight(bufferB, count, bufferA, maxVertices, rect.Right);
            count = ClipTop(bufferA, count, bufferB, maxVertices, rect.Top);
            count = ClipBottom(bufferB, count, bufferA, maxVertices, rect.Bottom);

            int resultCount = Math.Min(count, output.Length);
            for (int i = 0; i < resultCount; i++)
            {
                output[i] = bufferA[i];
            }

            return resultCount;
        }
        finally
        {
            NativeMemory.Free(bufferA);
            NativeMemory.Free(bufferB);
        }
    }

    /// <summary>
    /// Clips a subject polygon against a convex clipping polygon using Sutherland-Hodgman.
    /// Backed by NativeMemory unmanaged buffers.
    /// </summary>
    public static int ClipConvex(
        ReadOnlySpan<PointF> subject,
        ReadOnlySpan<PointF> clipPolygon,
        Span<PointF> output)
    {
        if (subject.Length < 3 || clipPolygon.Length < 3) return 0;

        int maxVertices = Math.Max((subject.Length + clipPolygon.Length) * 4, 128);
        PointF* bufIn = (PointF*)NativeMemory.Alloc((nuint)(maxVertices * sizeof(PointF)));
        PointF* bufOut = (PointF*)NativeMemory.Alloc((nuint)(maxVertices * sizeof(PointF)));

        try
        {
            fixed (PointF* pSub = subject)
            {
                Buffer.MemoryCopy(pSub, bufIn, maxVertices * sizeof(PointF), subject.Length * sizeof(PointF));
            }

            int inCount = subject.Length;

            for (int c = 0; c < clipPolygon.Length; c++)
            {
                PointF cp1 = clipPolygon[c];
                PointF cp2 = clipPolygon[(c + 1) % clipPolygon.Length];
                PointF edgeVec = cp2 - cp1;

                int outCount = 0;
                if (inCount == 0) break;

                PointF s = bufIn[inCount - 1];
                for (int i = 0; i < inCount; i++)
                {
                    PointF e = bufIn[i];

                    bool eInside = IsInside(edgeVec, cp1, e);
                    bool sInside = IsInside(edgeVec, cp1, s);

                    if (eInside)
                    {
                        if (!sInside)
                        {
                            if (outCount < maxVertices)
                                bufOut[outCount++] = LineIntersection(cp1, cp2, s, e);
                        }
                        if (outCount < maxVertices)
                            bufOut[outCount++] = e;
                    }
                    else if (sInside)
                    {
                        if (outCount < maxVertices)
                            bufOut[outCount++] = LineIntersection(cp1, cp2, s, e);
                    }
                    s = e;
                }

                // Swap pointers for next edge
                PointF* temp = bufIn;
                bufIn = bufOut;
                bufOut = temp;
                inCount = outCount;
            }

            int finalCount = Math.Min(inCount, output.Length);
            for (int i = 0; i < finalCount; i++)
            {
                output[i] = bufIn[i];
            }

            return finalCount;
        }
        finally
        {
            NativeMemory.Free(bufIn);
            NativeMemory.Free(bufOut);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInside(PointF edge, PointF edgeStart, PointF pt)
    {
        // Cross product determines which side of oriented edge line pt lies on
        return (edge.X * (pt.Y - edgeStart.Y) - edge.Y * (pt.X - edgeStart.X)) >= 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PointF LineIntersection(PointF a1, PointF a2, PointF b1, PointF b2)
    {
        float dAx = a2.X - a1.X;
        float dAy = a2.Y - a1.Y;
        float dBx = b2.X - b1.X;
        float dBy = b2.Y - b1.Y;

        float denom = dAx * dBy - dAy * dBx;
        if (MathF.Abs(denom) < 1e-6f) return b1;

        float num = (b1.X - a1.X) * dBy - (b1.Y - a1.Y) * dBx;
        float t = num / denom;
        return new PointF(a1.X + t * dAx, a1.Y + t * dAy);
    }

    private static int ClipLeft(PointF* src, int srcCount, PointF* dst, int dstCapacity, float left)
    {
        if (srcCount == 0) return 0;
        int dstCount = 0;
        PointF s = src[srcCount - 1];
        for (int i = 0; i < srcCount; i++)
        {
            PointF e = src[i];
            bool eIn = e.X >= left;
            bool sIn = s.X >= left;
            if (eIn)
            {
                if (!sIn && dstCount < dstCapacity)
                {
                    float t = (left - s.X) / (e.X - s.X);
                    dst[dstCount++] = new PointF(left, s.Y + t * (e.Y - s.Y));
                }
                if (dstCount < dstCapacity) dst[dstCount++] = e;
            }
            else if (sIn && dstCount < dstCapacity)
            {
                float t = (left - s.X) / (e.X - s.X);
                dst[dstCount++] = new PointF(left, s.Y + t * (e.Y - s.Y));
            }
            s = e;
        }
        return dstCount;
    }

    private static int ClipRight(PointF* src, int srcCount, PointF* dst, int dstCapacity, float right)
    {
        if (srcCount == 0) return 0;
        int dstCount = 0;
        PointF s = src[srcCount - 1];
        for (int i = 0; i < srcCount; i++)
        {
            PointF e = src[i];
            bool eIn = e.X <= right;
            bool sIn = s.X <= right;
            if (eIn)
            {
                if (!sIn && dstCount < dstCapacity)
                {
                    float t = (right - s.X) / (e.X - s.X);
                    dst[dstCount++] = new PointF(right, s.Y + t * (e.Y - s.Y));
                }
                if (dstCount < dstCapacity) dst[dstCount++] = e;
            }
            else if (sIn && dstCount < dstCapacity)
            {
                float t = (right - s.X) / (e.X - s.X);
                dst[dstCount++] = new PointF(right, s.Y + t * (e.Y - s.Y));
            }
            s = e;
        }
        return dstCount;
    }

    private static int ClipTop(PointF* src, int srcCount, PointF* dst, int dstCapacity, float top)
    {
        if (srcCount == 0) return 0;
        int dstCount = 0;
        PointF s = src[srcCount - 1];
        for (int i = 0; i < srcCount; i++)
        {
            PointF e = src[i];
            bool eIn = e.Y >= top;
            bool sIn = s.Y >= top;
            if (eIn)
            {
                if (!sIn && dstCount < dstCapacity)
                {
                    float t = (top - s.Y) / (e.Y - s.Y);
                    dst[dstCount++] = new PointF(s.X + t * (e.X - s.X), top);
                }
                if (dstCount < dstCapacity) dst[dstCount++] = e;
            }
            else if (sIn && dstCount < dstCapacity)
            {
                float t = (top - s.Y) / (e.Y - s.Y);
                dst[dstCount++] = new PointF(s.X + t * (e.X - s.X), top);
            }
            s = e;
        }
        return dstCount;
    }

    private static int ClipBottom(PointF* src, int srcCount, PointF* dst, int dstCapacity, float bottom)
    {
        if (srcCount == 0) return 0;
        int dstCount = 0;
        PointF s = src[srcCount - 1];
        for (int i = 0; i < srcCount; i++)
        {
            PointF e = src[i];
            bool eIn = e.Y <= bottom;
            bool sIn = s.Y <= bottom;
            if (eIn)
            {
                if (!sIn && dstCount < dstCapacity)
                {
                    float t = (bottom - s.Y) / (e.Y - s.Y);
                    dst[dstCount++] = new PointF(s.X + t * (e.X - s.X), bottom);
                }
                if (dstCount < dstCapacity) dst[dstCount++] = e;
            }
            else if (sIn && dstCount < dstCapacity)
            {
                float t = (bottom - s.Y) / (e.Y - s.Y);
                dst[dstCount++] = new PointF(s.X + t * (e.X - s.X), bottom);
            }
            s = e;
        }
        return dstCount;
    }


    /// <summary>
    /// Computes winding number of point relative to polygon.
    /// Positive or negative winding for NonZero, or even/odd count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeWindingNumber(PointF pt, ReadOnlySpan<PointF> polygon)
    {
        int winding = 0;
        int n = polygon.Length;

        for (int i = 0; i < n; i++)
        {
            PointF p1 = polygon[i];
            PointF p2 = polygon[(i + 1) % n];

            if (p1.Y <= pt.Y)
            {
                if (p2.Y > pt.Y) // Upward crossing
                {
                    float cross = (p2.X - p1.X) * (pt.Y - p1.Y) - (pt.X - p1.X) * (p2.Y - p1.Y);
                    if (cross > 0f) winding++;
                }
            }
            else
            {
                if (p2.Y <= pt.Y) // Downward crossing
                {
                    float cross = (p2.X - p1.X) * (pt.Y - p1.Y) - (pt.X - p1.X) * (p2.Y - p1.Y);
                    if (cross < 0f) winding--;
                }
            }
        }

        return winding;
    }

    /// <summary>
    /// Tests whether a point lies inside a polygon according to the specified winding rule.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPointInPolygon(PointF pt, ReadOnlySpan<PointF> polygon, WindingRule rule)
    {
        int w = ComputeWindingNumber(pt, polygon);
        return rule switch
        {
            WindingRule.NonZero => w != 0,
            WindingRule.EvenOdd => (w & 1) != 0,
            _ => w != 0
        };
    }
}
