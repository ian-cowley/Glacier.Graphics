namespace Glacier.Graphics.Raster;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Glacier.Graphics.Vector;

/// <summary>
/// Coarse tile classification for analytical tile-based compute rasterization.
/// </summary>
public enum TileClassification : byte
{
    Empty,   // 0 segments intersecting tile, outside polygon
    Full,    // 0 segments intersecting tile, fully inside polygon (fast solid shade)
    Complex  // Contains 1 or more boundary segments (fine analytical area evaluation with left-backdrop winding)
}

/// <summary>
/// 16x16 Tile Compute Rasterizer executing the 3-stage analytical pipeline:
/// Stage 1: Segment Binning into 16x16 pixel tiles (plus left-of-tile backdrop segments)
/// Stage 2: Coarse Tile Classification (Empty, Full, Complex) supporting both CW and CCW windings
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
        public readonly List<int> BackdropSegmentIndices = new();
    }

    public void Rasterize(
        LinearFramebuffer framebuffer,
        VectorPath path,
        Rgba32 color,
        WindingRule rule = WindingRule.NonZero)
    {
        if (framebuffer == null || path == null || path.PointCount < 3 || color.A == 0) return;

        int tilesX = (framebuffer.Width + TileSize - 1) / TileSize;
        int tilesY = (framebuffer.Height + TileSize - 1) / TileSize;

        var contours = path.Flatten(0.25f);
        var segments = new List<PathSegment>();

        foreach (var contour in contours)
        {
            int count = contour.Count;
            if (count < 2) continue;

            // Emit closed contour edges consistently with CpuRasterizer so both CW and CCW contours
            // (with or without explicit PathVerb.Close) produce watertight winding numbers.
            for (int i = 0; i < count; i++)
            {
                PointF p0 = contour[i];
                PointF p1 = contour[(i + 1) % count];
                if (p0 == p1) continue;
                segments.Add(new PathSegment(p0, p1));
            }
        }

        if (segments.Count == 0) return;

        TileData[] tiles = Stage1_BinSegments(tilesX, tilesY, segments);
        Stage2_ClassifyTiles(tiles, tilesX, tilesY, contours, rule);
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
            if (MathF.Abs(seg.P0.Y - seg.P1.Y) < 1e-6f && seg.Bounds.Top == seg.Bounds.Bottom)
            {
                // Horizontal segment still marks boundary tiles Complex so interior/exterior split tiles aren't misclassified Full
            }

            if (seg.Bounds.Bottom <= 0f || seg.Bounds.Top >= tilesY * TileSize)
            {
                continue;
            }

            int minTY = Math.Clamp((int)MathF.Floor(seg.Bounds.Top / TileSize), 0, tilesY - 1);
            int maxTY = Math.Clamp((int)MathF.Floor(MathF.Max(seg.Bounds.Top, MathF.BitDecrement(seg.Bounds.Bottom)) / TileSize), 0, tilesY - 1);

            int rawMinTX = (int)MathF.Floor(seg.Bounds.Left / TileSize);
            int rawMaxTX = (int)MathF.Floor(seg.Bounds.Right / TileSize);

            for (int ty = minTY; ty <= maxTY; ty++)
            {
                int rowOffset = ty * tilesX;

                if (rawMaxTX >= 0 && rawMinTX < tilesX)
                {
                    int minTX = Math.Clamp(rawMinTX, 0, tilesX - 1);
                    int maxTX = Math.Clamp(rawMaxTX, 0, tilesX - 1);
                    for (int tx = minTX; tx <= maxTX; tx++)
                    {
                        tiles[rowOffset + tx].SegmentIndices.Add(s);
                    }
                }

                // Any tile strictly to the right of rawMaxTX on this tile row receives this segment's left-backdrop winding
                int backdropStartTX = Math.Max(0, rawMaxTX + 1);
                for (int tx = backdropStartTX; tx < tilesX; tx++)
                {
                    tiles[rowOffset + tx].BackdropSegmentIndices.Add(s);
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
                float centerX = (tile.TileX + 0.5f) * TileSize;
                float centerY = (tile.TileY + 0.5f) * TileSize;
                PointF center = new(centerX, centerY);

                int totalWinding = 0;
                foreach (var contour in contours)
                {
                    totalWinding += PolygonClipper.ComputeWindingNumber(center, CollectionsMarshal.AsSpan(contour));
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

                // 1. Accumulate exact left-of-tile backdrop winding from segments strictly to the left of this tile
                for (int b = 0; b < tile.BackdropSegmentIndices.Count; b++)
                {
                    var seg = segments[tile.BackdropSegmentIndices[b]];
                    if (seg.Bounds.Bottom <= lineTop || seg.Bounds.Top >= lineBottom)
                        continue;

                    float clampedY1 = Math.Clamp(seg.P0.Y, lineTop, lineBottom);
                    float clampedY2 = Math.Clamp(seg.P1.Y, lineTop, lineBottom);
                    float signedDy = clampedY2 - clampedY1;
                    if (MathF.Abs(signedDy) < 1e-5f) continue;

                    areaDeltas[0] += signedDy;
                    coverageDeltas[0] += signedDy;
                }

                // 2. Accumulate boundary segments intersecting this tile
                for (int s = 0; s < tile.SegmentIndices.Count; s++)
                {
                    var seg = segments[tile.SegmentIndices[s]];
                    if (seg.Bounds.Bottom <= lineTop || seg.Bounds.Top >= lineBottom)
                        continue;

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

                var row = fb.GetRowSpan(y);
                float accumCoverage = 0f;

                for (int px = 0; px < TileSize; px++)
                {
                    int x = startPixelX + px;
                    if (x >= fb.Width) break;

                    float pixelArea = accumCoverage + areaDeltas[px];
                    accumCoverage += coverageDeltas[px];

                    float alpha = rule switch
                    {
                        WindingRule.NonZero => Math.Clamp(MathF.Abs(pixelArea), 0f, 1f),
                        WindingRule.EvenOdd => Math.Clamp(MathF.Abs(pixelArea - 2.0f * MathF.Round(pixelArea * 0.5f)), 0f, 1f),
                        _ => Math.Clamp(MathF.Abs(pixelArea), 0f, 1f)
                    };

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
