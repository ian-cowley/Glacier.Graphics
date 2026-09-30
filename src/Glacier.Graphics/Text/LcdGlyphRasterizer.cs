namespace Glacier.Graphics.Text;

using System;
using System.Collections.Generic;
using Glacier.Graphics.Vector;

/// <summary>
/// Rasterized sub-pixel LCD glyph bitmap with gamma-corrected RGB coverage values.
/// </summary>
public sealed class LcdGlyphBitmap
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Data { get; } // 3 bytes per pixel (R, G, B coverage)
    public float BearingX { get; set; }
    public float BearingY { get; set; }
    public float Advance { get; set; }

    public LcdGlyphBitmap(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = width * 3;
        Data = new byte[Stride * height];
    }
}

/// <summary>
/// High-performance sub-pixel LCD glyph rasterizer with horizontal 3x supersampling,
/// FIR color-fringe filtering, and sRGB gamma correction.
/// </summary>
public static class LcdGlyphRasterizer
{
    private static readonly byte[] GammaCorrectionTable = CreateGammaTable();

    private static byte[] CreateGammaTable()
    {
        byte[] table = new byte[256];
        const float gamma = 2.2f;
        for (int i = 0; i < 256; i++)
        {
            float norm = i / 255.0f;
            float corrected = MathF.Pow(norm, 1.0f / gamma);
            table[i] = (byte)Math.Clamp((int)MathF.Round(corrected * 255.0f), 0, 255);
        }
        return table;
    }

    /// <summary>
    /// Rasterizes a glyph outline into an LCD sub-pixel RGB coverage bitmap.
    /// </summary>
    public static LcdGlyphBitmap Rasterize(
        VectorPath glyphPath,
        float scale,
        float fractionalX = 0f,
        WindingRule rule = WindingRule.NonZero)
    {
        if (glyphPath == null || glyphPath.PointCount == 0)
        {
            return new LcdGlyphBitmap(0, 0);
        }

        // Quantize fractional subpixel position to 1/4 pixel increments
        fractionalX = MathF.Round(fractionalX * 4f) / 4f;

        // Transform glyph coordinates: flip Y (fonts are Y-up, screen is Y-down) and scale
        RectF bounds = glyphPath.ComputeBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return new LcdGlyphBitmap(0, 0);
        }

        float scaledMinX = bounds.Left * scale + fractionalX;
        float scaledMaxX = bounds.Right * scale + fractionalX;
        float scaledMinY = -bounds.Bottom * scale; // Y-up to Y-down
        float scaledMaxY = -bounds.Top * scale;

        int minPixelX = (int)MathF.Floor(scaledMinX);
        int maxPixelX = (int)MathF.Ceiling(scaledMaxX);
        int minPixelY = (int)MathF.Floor(scaledMinY);
        int maxPixelY = (int)MathF.Ceiling(scaledMaxY);

        int pixelWidth = Math.Max(1, maxPixelX - minPixelX + 2);
        int pixelHeight = Math.Max(1, maxPixelY - minPixelY + 2);

        // Subpixel resolution: 3 horizontal sub-pixels per physical pixel
        int subpixelWidth = pixelWidth * 3;

        // Transform path into subpixel coordinate space
        // Subpixel X: 3 * (scale * x + fractionalX - minPixelX)
        // Y: scale * (-y) - minPixelY
        var subpixelPath = new VectorPath(glyphPath);
        Matrix3x2 xform = new Matrix3x2(
            scale * 3f, 0f,
            0f, -scale,
            (-minPixelX + fractionalX) * 3f,
            -minPixelY);
        subpixelPath.Transform(xform);

        var contours = subpixelPath.Flatten(0.2f);

        // Supersampled coverage buffer
        byte[] coverage = new byte[subpixelWidth * pixelHeight];

        // Rasterize contours into coverage buffer using scanline winding
        RasterizeCoverage(contours, subpixelWidth, pixelHeight, coverage, rule);

        // Apply 5-tap FIR filter to eliminate color fringing and apply gamma correction
        var lcdBitmap = new LcdGlyphBitmap(pixelWidth, pixelHeight)
        {
            BearingX = minPixelX - fractionalX,
            BearingY = minPixelY
        };

        ApplyLcdFilterAndGamma(coverage, subpixelWidth, pixelHeight, lcdBitmap.Data);

        return lcdBitmap;
    }

    private static void RasterizeCoverage(
        List<List<PointF>> contours,
        int width,
        int height,
        byte[] coverage,
        WindingRule rule)
    {
        // 4x vertical supersampling for smooth antialiasing
        const int vSub = 4;
        int vHeight = height * vSub;

        // Build edge list
        var edges = new List<Edge>();
        foreach (var contour in contours)
        {
            for (int i = 0; i < contour.Count; i++)
            {
                PointF p1 = contour[i];
                PointF p2 = contour[(i + 1) % contour.Count];
                if (MathF.Abs(p1.Y - p2.Y) < 1e-4f) continue; // Horizontal edge

                float y1 = p1.Y * vSub;
                float y2 = p2.Y * vSub;

                if (y1 < y2)
                {
                    edges.Add(new Edge(p1.X, y1, p2.X, y2, 1));
                }
                else
                {
                    edges.Add(new Edge(p2.X, y2, p1.X, y1, -1));
                }
            }
        }

        // Active edge table scanline evaluation
        edges.Sort((a, b) => a.YMin.CompareTo(b.YMin));

        var intersections = new List<float>(32);
        var active = new List<Edge>(edges.Count);
        int edgeIdx = 0;

        for (int y = 0; y < vHeight; y++)
        {
            float scanY = y + 0.5f;

            // Add newly active edges
            while (edgeIdx < edges.Count && edges[edgeIdx].YMin <= scanY)
            {
                active.Add(edges[edgeIdx++]);
            }

            // Remove expired edges
            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].YMax <= scanY)
                {
                    active.RemoveAt(i);
                }
            }

            if (active.Count == 0) continue;

            // Compute intersections
            intersections.Clear();
            for (int i = 0; i < active.Count; i++)
            {
                var edge = active[i];
                float t = (scanY - edge.YMin) / (edge.YMax - edge.YMin);
                float x = edge.XMin + t * (edge.XMax - edge.XMin);
                intersections.Add(x);
            }

            intersections.Sort();

            int targetRow = (y / vSub) * width;

            // Fill spans
            for (int i = 0; i < intersections.Count - 1; i += 2)
            {
                int xStart = Math.Max(0, (int)MathF.Round(intersections[i]));
                int xEnd = Math.Min(width - 1, (int)MathF.Round(intersections[i + 1]));

                for (int x = xStart; x <= xEnd; x++)
                {
                    int idx = targetRow + x;
                    int cur = coverage[idx] + (255 / vSub);
                    coverage[idx] = (byte)Math.Min(255, cur);
                }
            }
        }
    }

    private static void ApplyLcdFilterAndGamma(byte[] rawSubpixels, int subWidth, int height, byte[] outLcd)
    {
        // 5-tap filter weights: [1, 4, 6, 4, 1] / 16
        for (int y = 0; y < height; y++)
        {
            int rowIn = y * subWidth;
            int rowOut = y * subWidth;

            for (int x = 0; x < subWidth; x++)
            {
                int c_m2 = rawSubpixels[rowIn + Math.Clamp(x - 2, 0, subWidth - 1)];
                int c_m1 = rawSubpixels[rowIn + Math.Clamp(x - 1, 0, subWidth - 1)];
                int c_0  = rawSubpixels[rowIn + x];
                int c_p1 = rawSubpixels[rowIn + Math.Clamp(x + 1, 0, subWidth - 1)];
                int c_p2 = rawSubpixels[rowIn + Math.Clamp(x + 2, 0, subWidth - 1)];

                int filtered = (c_m2 * 1 + c_m1 * 4 + c_0 * 6 + c_p1 * 4 + c_p2 * 1 + 8) >> 4;
                outLcd[rowOut + x] = GammaCorrectionTable[Math.Clamp(filtered, 0, 255)];
            }
        }
    }

    private readonly struct Edge
    {
        public readonly float XMin;
        public readonly float YMin;
        public readonly float XMax;
        public readonly float YMax;
        public readonly int Direction;

        public Edge(float x1, float y1, float x2, float y2, int direction)
        {
            XMin = x1;
            YMin = y1;
            XMax = x2;
            YMax = y2;
            Direction = direction;
        }
    }
}
