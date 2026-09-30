namespace Glacier.Graphics.Raster;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Glacier.Graphics.Vector;

/// <summary>
/// Coarse tile classification for analytical tile-based compute rasterization.
/// </summary>
public enum TileClassification : byte
{
    Empty,   // 0 segments, fully outside polygon
    Full,    // 0 segments, fully inside filled polygon (fast solid shade)
    Complex  // Contains 1 or more boundary segments (fine area evaluation)
}

/// <summary>
/// 16x16 Tile Compute Rasterizer executing the 3-stage analytical pipeline:
/// Stage 1: Segment Binning into 16x16 pixel tiles
/// Stage 2: Coarse Tile Classification (Empty, Full, Complex)
/// Stage 3: Fine Pixel Coverage Evaluation via exact trapezoidal area integration
/// </summary>
public sealed class TileComputeRasterizer
{
    public const int TileSize = 16;

    public readonly struct PathSegment
    {
        public readonly PointF P0;
        public readonly PointF P1;
        public readonly RectF Bounds;

        public PathSegment(PointF p0, PointF p1)
        {
            P0 = p0;
            P1 = p1;
            float minX = MathF.Min(p0.X, p1.X);
            float maxX = MathF.Max(p0.X, p1.X);
            float minY = MathF.Min(p0.Y, p1.Y);
            float maxY = MathF.Max(p0.Y, p1.Y);
            Bounds = RectF.FromLTRB(minX, minY, maxX, maxY);
        }
    }

    public sealed class TileData
    {
        public int TileX;
        public int TileY;
        public TileClassification Classification;
        public readonly List<int> SegmentIndices = new();
    }

    public void Rasterize(
        LinearFramebuffer framebuffer,
        VectorPath path,
        Rgba32 color,
        WindingRule rule = WindingRule.NonZero)
    {
        if (framebuffer == null || path == null || path.PointCount < 3) return;

        int tilesX = (framebuffer.Width + TileSize - 1) / TileSize;
        int tilesY = (framebuffer.Height + TileSize - 1) / TileSize;
        int totalTiles = tilesX * tilesY;

        var contours = path.Flatten(0.25f);
        var segments = new List<PathSegment>();

        foreach (var contour in contours)
        {
            for (int i = 0; i < contour.Count - 1; i++)
            {
                segments.Add(new PathSegment(contour[i], contour[i + 1]));
            }
        }

        if (segments.Count == 0) return;

        // Stage 1: Segment Binning
        TileData[] tiles = Stage1_BinSegments(tilesX, tilesY, segments);

        // Stage 2: Coarse Tile Classification
        Stage2_ClassifyTiles(tiles, tilesX, tilesY, contours, rule);

        // Stage 3: Fine Pixel Coverage Evaluation & Blending
        Stage3_EvaluateCoverage(framebuffer, tiles, tilesX, tilesY, segments, color, rule);
    }

    public static TileData[] Stage1_BinSegments(int tilesX, int tilesY, List<PathSegment> segments)
    {
        int totalTiles = tilesX * tilesY;
        var tiles = new TileData[totalTiles];
        for (int i = 0; i < totalTiles; i++)
        {
            tiles[i] = new TileData
            {
                TileX = i % tilesX,
                TileY = i / tilesX
            };
        }

        for (int s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            int minTX = Math.Clamp((int)MathF.Floor(seg.Bounds.Left / TileSize), 0, tilesX - 1);
            int maxTX = Math.Clamp((int)MathF.Floor(seg.Bounds.Right / TileSize), 0, tilesX - 1);
            int minTY = Math.Clamp((int)MathF.Floor(seg.Bounds.Top / TileSize), 0, tilesY - 1);
            int maxTY = Math.Clamp((int)MathF.Floor(seg.Bounds.Bottom / TileSize), 0, tilesY - 1);

            for (int ty = minTY; ty <= maxTY; ty++)
            {
                int rowOffset = ty * tilesX;
                for (int tx = minTX; tx <= maxTX; tx++)
                {
                    tiles[rowOffset + tx].SegmentIndices.Add(s);
                }
            }
        }

        return tiles;
    }

    public static void Stage2_ClassifyTiles(
        TileData[] tiles,
        int tilesX,
        int tilesY,
        List<List<PointF>> contours,
        WindingRule rule)
    {
        Parallel.For(0, tiles.Length, i =>
        {
            var tile = tiles[i];
            if (tile.SegmentIndices.Count > 0)
            {
                tile.Classification = TileClassification.Complex;
            }
            else
            {
                // Test tile center for containment
                float centerX = (tile.TileX + 0.5f) * TileSize;
                float centerY = (tile.TileY + 0.5f) * TileSize;
                PointF center = new PointF(centerX, centerY);

                int totalWinding = 0;
                foreach (var contour in contours)
                {
                    totalWinding += PolygonClipper.ComputeWindingNumber(center, contour.ToArray());
                }

                bool isInside = rule switch
                {
                    WindingRule.NonZero => totalWinding != 0,
                    WindingRule.EvenOdd => (totalWinding & 1) != 0,
                    _ => totalWinding != 0
                };

                tile.Classification = isInside ? TileClassification.Full : TileClassification.Empty;
            }
        });
    }

    public static void Stage3_EvaluateCoverage(
        LinearFramebuffer fb,
        TileData[] tiles,
        int tilesX,
        int tilesY,
        List<PathSegment> segments,
        Rgba32 color,
        WindingRule rule)
    {
        Parallel.For(0, tiles.Length, i =>
        {
            var tile = tiles[i];
            if (tile.Classification == TileClassification.Empty) return;

            int startPixelX = tile.TileX * TileSize;
            int startPixelY = tile.TileY * TileSize;

            if (tile.Classification == TileClassification.Full)
            {
                // Solid fast shade entire 16x16 tile
                for (int py = 0; py < TileSize; py++)
                {
                    int y = startPixelY + py;
                    if (y >= fb.Height) break;
                    var row = fb.GetRowSpan(y);

                    for (int px = 0; px < TileSize; px++)
                    {
                        int x = startPixelX + px;
                        if (x >= fb.Width) break;
                        BlendPixel(ref row[x], color, 1.0f);
                    }
                }
                return;
            }

            // Complex Tile: evaluate per-pixel analytical trapezoidal coverage
            Span<float> coverageDeltas = stackalloc float[TileSize + 1];
            Span<float> areaDeltas = stackalloc float[TileSize + 1];

            for (int py = 0; py < TileSize; py++)
            {
                int y = startPixelY + py;
                if (y >= fb.Height) break;

                coverageDeltas.Clear();
                areaDeltas.Clear();

                float lineTop = y;
                float lineBottom = y + 1.0f;

                // Accumulate trapezoidal coverage deltas for segments crossing this pixel row
                for (int s = 0; s < tile.SegmentIndices.Count; s++)
                {
                    var seg = segments[tile.SegmentIndices[s]];
                    if (seg.Bounds.Bottom <= lineTop || seg.Bounds.Top >= lineBottom)
                        continue;

                    // Clip segment Y to [lineTop, lineBottom]
                    float y1 = seg.P0.Y;
                    float y2 = seg.P1.Y;
                    float x1 = seg.P0.X;
                    float x2 = seg.P1.X;

                    float clampedY1 = Math.Clamp(y1, lineTop, lineBottom);
                    float clampedY2 = Math.Clamp(y2, lineTop, lineBottom);
                    if (MathF.Abs(clampedY1 - clampedY2) < 1e-5f) continue;

                    float dy = y2 - y1;
                    float t1 = (clampedY1 - y1) / dy;
                    float t2 = (clampedY2 - y1) / dy;
                    float clampedX1 = x1 + t1 * (x2 - x1) - startPixelX;
                    float clampedX2 = x1 + t2 * (x2 - x1) - startPixelX;

                    AnalyticalCoverage.AccumulateSegment(
                        coverageDeltas, areaDeltas, TileSize,
                        clampedX1, clampedY1 - lineTop, clampedX2, clampedY2 - lineTop);
                }

                // Prefix sum to integrate pixel coverage across scanline
                var row = fb.GetRowSpan(y);
                float accumCoverage = 0f;

                for (int px = 0; px < TileSize; px++)
                {
                    int x = startPixelX + px;
                    if (x >= fb.Width) break;

                    float pixelArea = accumCoverage + areaDeltas[px];
                    accumCoverage += coverageDeltas[px];

                    float alpha = Math.Clamp(MathF.Abs(pixelArea), 0f, 1f);
                    if (alpha > 0.001f)
                    {
                        BlendPixel(ref row[x], color, alpha);
                    }
                }
            }
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
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
