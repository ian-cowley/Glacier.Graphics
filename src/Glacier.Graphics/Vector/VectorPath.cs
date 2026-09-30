namespace Glacier.Graphics.Vector;

using System;
using System.Collections.Generic;

/// <summary>
/// High-performance 2D vector path representation supporting lines, quadratic, and cubic Bézier curves.
/// </summary>
public sealed class VectorPath
{
    private PathVerb[] _verbs;
    private PointF[] _points;
    private int _verbCount;
    private int _pointCount;

    public WindingRule FillRule { get; set; } = WindingRule.NonZero;

    public int VerbCount => _verbCount;
    public int PointCount => _pointCount;

    public ReadOnlySpan<PathVerb> Verbs => _verbs.AsSpan(0, _verbCount);
    public ReadOnlySpan<PointF> Points => _points.AsSpan(0, _pointCount);

    public VectorPath(int initialCapacity = 32)
    {
        _verbs = new PathVerb[initialCapacity];
        _points = new PointF[initialCapacity * 2];
    }

    public VectorPath(VectorPath source)
    {
        _verbCount = source._verbCount;
        _pointCount = source._pointCount;
        _verbs = new PathVerb[_verbCount];
        _points = new PointF[_pointCount];
        source.Verbs.CopyTo(_verbs);
        source.Points.CopyTo(_points);
        FillRule = source.FillRule;
    }

    public void Clear()
    {
        _verbCount = 0;
        _pointCount = 0;
    }

    public void MoveTo(float x, float y)
    {
        EnsureVerbCapacity(1);
        EnsurePointCapacity(1);
        _verbs[_verbCount++] = PathVerb.MoveTo;
        _points[_pointCount++] = new PointF(x, y);
    }

    public void MoveTo(PointF p) => MoveTo(p.X, p.Y);

    public void LineTo(float x, float y)
    {
        EnsureVerbCapacity(1);
        EnsurePointCapacity(1);
        _verbs[_verbCount++] = PathVerb.LineTo;
        _points[_pointCount++] = new PointF(x, y);
    }

    public void LineTo(PointF p) => LineTo(p.X, p.Y);

    public void QuadTo(float x1, float y1, float x2, float y2)
    {
        EnsureVerbCapacity(1);
        EnsurePointCapacity(2);
        _verbs[_verbCount++] = PathVerb.QuadTo;
        _points[_pointCount++] = new PointF(x1, y1);
        _points[_pointCount++] = new PointF(x2, y2);
    }

    public void QuadTo(PointF p1, PointF p2) => QuadTo(p1.X, p1.Y, p2.X, p2.Y);

    public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        EnsureVerbCapacity(1);
        EnsurePointCapacity(3);
        _verbs[_verbCount++] = PathVerb.CubicTo;
        _points[_pointCount++] = new PointF(x1, y1);
        _points[_pointCount++] = new PointF(x2, y2);
        _points[_pointCount++] = new PointF(x3, y3);
    }

    public void CubicTo(PointF p1, PointF p2, PointF p3) => CubicTo(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);

    public void Close()
    {
        if (_verbCount > 0 && _verbs[_verbCount - 1] != PathVerb.Close)
        {
            EnsureVerbCapacity(1);
            _verbs[_verbCount++] = PathVerb.Close;
        }
    }

    public void AddLine(float x1, float y1, float x2, float y2)
    {
        MoveTo(x1, y1);
        LineTo(x2, y2);
    }

    public void AddRect(float x, float y, float width, float height)
    {
        MoveTo(x, y);
        LineTo(x + width, y);
        LineTo(x + width, y + height);
        LineTo(x, y + height);
        Close();
    }

    public void AddRect(in RectF rect) => AddRect(rect.X, rect.Y, rect.Width, rect.Height);

    public void AddOval(float x, float y, float width, float height)
    {
        // Approximate ellipse using 4 cubic Béziers (standard kappa constant = 4/3 * (sqrt(2)-1) ~ 0.55228475)
        float rx = width * 0.5f;
        float ry = height * 0.5f;
        float cx = x + rx;
        float cy = y + ry;
        const float kappa = 0.5522847498f;
        float kx = rx * kappa;
        float ky = ry * kappa;

        MoveTo(cx + rx, cy);
        CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
        CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
        CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
        CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
        Close();
    }

    public void AddCircle(float cx, float cy, float radius)
    {
        AddOval(cx - radius, cy - radius, radius * 2f, radius * 2f);
    }

    public void AddPath(VectorPath path)
    {
        EnsureVerbCapacity(path._verbCount);
        EnsurePointCapacity(path._pointCount);

        Array.Copy(path._verbs, 0, _verbs, _verbCount, path._verbCount);
        Array.Copy(path._points, 0, _points, _pointCount, path._pointCount);

        _verbCount += path._verbCount;
        _pointCount += path._pointCount;
    }

    public void Transform(in Matrix3x2 matrix)
    {
        for (int i = 0; i < _pointCount; i++)
        {
            _points[i] = Matrix3x2.TransformPoint(matrix, _points[i]);
        }
    }

    public RectF ComputeBounds()
    {
        if (_pointCount == 0) return RectF.Empty;

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        for (int i = 0; i < _pointCount; i++)
        {
            PointF p = _points[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        return RectF.FromLTRB(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Flattens all curves into linear segments according to the specified tolerance.
    /// Returns a list of discrete polygonal contours.
    /// </summary>
    public List<List<PointF>> Flatten(float tolerance = 0.5f)
    {
        var contours = new List<List<PointF>>();
        List<PointF>? currentContour = null;

        int ptIdx = 0;
        PointF currentPoint = PointF.Zero;
        PointF contourStart = PointF.Zero;

        for (int v = 0; v < _verbCount; v++)
        {
            PathVerb verb = _verbs[v];
            switch (verb)
            {
                case PathVerb.MoveTo:
                    currentPoint = _points[ptIdx++];
                    contourStart = currentPoint;
                    currentContour = new List<PointF> { currentPoint };
                    contours.Add(currentContour);
                    break;

                case PathVerb.LineTo:
                {
                    PointF pt = _points[ptIdx++];
                    if (currentContour == null)
                    {
                        currentContour = new List<PointF> { currentPoint };
                        contours.Add(currentContour);
                    }
                    currentContour.Add(pt);
                    currentPoint = pt;
                    break;
                }

                case PathVerb.QuadTo:
                {
                    PointF p1 = _points[ptIdx++];
                    PointF p2 = _points[ptIdx++];
                    if (currentContour == null)
                    {
                        currentContour = new List<PointF> { currentPoint };
                        contours.Add(currentContour);
                    }
                    FlattenQuad(currentContour, currentPoint, p1, p2, tolerance);
                    currentPoint = p2;
                    break;
                }

                case PathVerb.CubicTo:
                {
                    PointF p1 = _points[ptIdx++];
                    PointF p2 = _points[ptIdx++];
                    PointF p3 = _points[ptIdx++];
                    if (currentContour == null)
                    {
                        currentContour = new List<PointF> { currentPoint };
                        contours.Add(currentContour);
                    }
                    FlattenCubic(currentContour, currentPoint, p1, p2, p3, tolerance);
                    currentPoint = p3;
                    break;
                }

                case PathVerb.Close:
                    if (currentContour != null && currentContour.Count > 0)
                    {
                        if (currentPoint != contourStart)
                        {
                            currentContour.Add(contourStart);
                        }
                    }
                    currentPoint = contourStart;
                    break;
            }
        }

        return contours;
    }

    private static void FlattenQuad(List<PointF> output, PointF p0, PointF p1, PointF p2, float tolerance)
    {
        // De Casteljau subdivision based on flatness
        float dx = p2.X - p0.X;
        float dy = p2.Y - p0.Y;
        float d = MathF.Abs((p1.X - p2.X) * dy - (p1.Y - p2.Y) * dx);
        if (d * d <= tolerance * tolerance * (dx * dx + dy * dy) || d <= 1e-4f)
        {
            output.Add(p2);
            return;
        }

        PointF p01 = (p0 + p1) * 0.5f;
        PointF p12 = (p1 + p2) * 0.5f;
        PointF p012 = (p01 + p12) * 0.5f;

        FlattenQuad(output, p0, p01, p012, tolerance);
        FlattenQuad(output, p012, p12, p2, tolerance);
    }

    private static void FlattenCubic(List<PointF> output, PointF p0, PointF p1, PointF p2, PointF p3, float tolerance)
    {
        // Subdivide if control points deviate from chord line
        float dx = p3.X - p0.X;
        float dy = p3.Y - p0.Y;
        float lenSq = dx * dx + dy * dy;
        float d1 = MathF.Abs((p1.X - p3.X) * dy - (p1.Y - p3.Y) * dx);
        float d2 = MathF.Abs((p2.X - p3.X) * dy - (p2.Y - p3.Y) * dx);

        if ((d1 + d2) * (d1 + d2) <= 4f * tolerance * tolerance * lenSq || (d1 + d2) <= 1e-4f)
        {
            output.Add(p3);
            return;
        }

        PointF p01 = (p0 + p1) * 0.5f;
        PointF p12 = (p1 + p2) * 0.5f;
        PointF p23 = (p2 + p3) * 0.5f;
        PointF p012 = (p01 + p12) * 0.5f;
        PointF p123 = (p12 + p23) * 0.5f;
        PointF p0123 = (p012 + p123) * 0.5f;

        FlattenCubic(output, p0, p01, p012, p0123, tolerance);
        FlattenCubic(output, p0123, p123, p23, p3, tolerance);
    }

    private void EnsureVerbCapacity(int additional)
    {
        if (_verbCount + additional > _verbs.Length)
        {
            int newCap = Math.Max(_verbs.Length * 2, _verbCount + additional);
            Array.Resize(ref _verbs, newCap);
        }
    }

    private void EnsurePointCapacity(int additional)
    {
        if (_pointCount + additional > _points.Length)
        {
            int newCap = Math.Max(_points.Length * 2, _pointCount + additional);
            Array.Resize(ref _points, newCap);
        }
    }
}
