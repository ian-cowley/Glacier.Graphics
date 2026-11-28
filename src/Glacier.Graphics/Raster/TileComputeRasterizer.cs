namespace Glacier.Graphics.Raster;

using System;
using System.Buffers;
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
/// 16x16 CPU Tile Compute Rasterizer executing a 3-stage analytical pipeline via <c>Parallel.For</c>:
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
        int totalTiles = tilesX * tilesY;
        if (totalTiles <= 0) return;

        var contours = path.Flatten(0.25f);
        int maxSegments = 0;
        foreach (var contour in contours)
        {
            if (contour.Count >= 2) maxSegments += contour.Count;
        }
        if (maxSegments == 0) return;

        PathSegment[] segArray = ArrayPool<PathSegment>.Shared.Rent(maxSegments);
        int[] segCounts = ArrayPool<int>.Shared.Rent(totalTiles);
        int[] backdropCounts = ArrayPool<int>.Shared.Rent(totalTiles);
        int[] segOffsets = ArrayPool<int>.Shared.Rent(totalTiles + 1);
        int[] backdropOffsets = ArrayPool<int>.Shared.Rent(totalTiles + 1);
        int[] segCursors = ArrayPool<int>.Shared.Rent(totalTiles);
        int[] backdropCursors = ArrayPool<int>.Shared.Rent(totalTiles);
        TileClassification[] classifications = ArrayPool<TileClassification>.Shared.Rent(totalTiles);
        int[]? flatSegIndices = null;
        int[]? flatBackdropIndices = null;

        try
        {
            int segCount = 0;
            foreach (var contour in contours)
            {
                int count = contour.Count;
                if (count < 2) continue;

                for (int i = 0; i < count; i++)
                {
                    PointF p0 = contour[i];
                    PointF p1 = contour[(i + 1) % count];
                    if (p0 == p1) continue;
                    segArray[segCount++] = new PathSegment(p0, p1);
                }
            }

            if (segCount == 0) return;

            segCounts.AsSpan(0, totalTiles).Clear();
            backdropCounts.AsSpan(0, totalTiles).Clear();

            float maxPixelY = tilesY * TileSize;

            // Pass 1: count per-tile segment and backdrop references
            for (int s = 0; s < segCount; s++)
            {
                ref readonly PathSegment seg = ref segArray[s];
                if (seg.Bounds.Bottom <= 0f || seg.Bounds.Top >= maxPixelY) continue;

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
                            segCounts[rowOffset + tx]++;
                        }
                    }

                    int backdropStartTX = Math.Max(0, rawMaxTX + 1);
                    for (int tx = backdropStartTX; tx < tilesX; tx++)
                    {
                        backdropCounts[rowOffset + tx]++;
                    }
                }
            }

            segOffsets[0] = 0;
            backdropOffsets[0] = 0;
            for (int i = 0; i < totalTiles; i++)
            {
                segCursors[i] = segOffsets[i];
                backdropCursors[i] = backdropOffsets[i];
                segOffsets[i + 1] = segOffsets[i] + segCounts[i];
                backdropOffsets[i + 1] = backdropOffsets[i] + backdropCounts[i];
            }

            int totalSegRefs = segOffsets[totalTiles];
            int totalBackdropRefs = backdropOffsets[totalTiles];
            flatSegIndices = ArrayPool<int>.Shared.Rent(Math.Max(1, totalSegRefs));
            flatBackdropIndices = ArrayPool<int>.Shared.Rent(Math.Max(1, totalBackdropRefs));

            // Pass 2: populate flat CSR tile index buffers
            for (int s = 0; s < segCount; s++)
            {
                ref readonly PathSegment seg = ref segArray[s];
                if (seg.Bounds.Bottom <= 0f || seg.Bounds.Top >= maxPixelY) continue;

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
                            int tileIdx = rowOffset + tx;
                            flatSegIndices[segCursors[tileIdx]++] = s;
                        }
                    }

                    int backdropStartTX = Math.Max(0, rawMaxTX + 1);
                    for (int tx = backdropStartTX; tx < tilesX; tx++)
                    {
                        int tileIdx = rowOffset + tx;
                        flatBackdropIndices[backdropCursors[tileIdx]++] = s;
                    }
                }
            }

            // Stage 2: Coarse classification
            Parallel.For(0, totalTiles, i =>
            {
                if (segCounts[i] > 0)
                {
                    classifications[i] = TileClassification.Complex;
                }
                else
                {
                    int tileX = i % tilesX;
                    int tileY = i / tilesX;
                    PointF center = new((tileX + 0.5f) * TileSize, (tileY + 0.5f) * TileSize);

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

                    classifications[i] = isInside ? TileClassification.Full : TileClassification.Empty;
                }
            });

            // Stage 3: Fine coverage evaluation
            Parallel.For(0, totalTiles, i =>
            {
                TileClassification cls = classifications[i];
                if (cls == TileClassification.Empty) return;

                int tileX = i % tilesX;
                int tileY = i / tilesX;
                int startPixelX = tileX * TileSize;
                int startPixelY = tileY * TileSize;

                if (cls == TileClassification.Full)
                {
                    for (int py = 0; py < TileSize; py++)
                    {
                        int y = startPixelY + py;
                        if (y >= framebuffer.Height) break;
                        var row = framebuffer.GetRowSpan(y);

                        for (int px = 0; px < TileSize; px++)
                        {
                            int x = startPixelX + px;
                            if (x >= framebuffer.Width) break;
                            ColorBlend.BlendPixel(ref row[x], color, 1.0f);
                        }
                    }
                    return;
                }

                ReadOnlySpan<int> tileSegs = flatSegIndices.AsSpan(segOffsets[i], segCounts[i]);
                ReadOnlySpan<int> tileBackdrops = flatBackdropIndices.AsSpan(backdropOffsets[i], backdropCounts[i]);
                EvaluateComplexTile(framebuffer, segArray, tileSegs, tileBackdrops, startPixelX, startPixelY, color, rule);
            });
        }
        finally
        {
            ArrayPool<PathSegment>.Shared.Return(segArray);
            ArrayPool<int>.Shared.Return(segCounts);
            ArrayPool<int>.Shared.Return(backdropCounts);
            ArrayPool<int>.Shared.Return(segOffsets);
            ArrayPool<int>.Shared.Return(backdropOffsets);
            ArrayPool<int>.Shared.Return(segCursors);
            ArrayPool<int>.Shared.Return(backdropCursors);
            ArrayPool<TileClassification>.Shared.Return(classifications);
            if (flatSegIndices != null) ArrayPool<int>.Shared.Return(flatSegIndices);
            if (flatBackdropIndices != null) ArrayPool<int>.Shared.Return(flatBackdropIndices);
        }
    }

    private static void EvaluateComplexTile(
        LinearFramebuffer fb,
        PathSegment[] segments,
        ReadOnlySpan<int> tileSegs,
        ReadOnlySpan<int> tileBackdrops,
        int startPixelX,
        int startPixelY,
        Rgba32 color,
        WindingRule rule)
    {
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

            for (int b = 0; b < tileBackdrops.Length; b++)
            {
                ref readonly PathSegment seg = ref segments[tileBackdrops[b]];
                if (seg.Bounds.Bottom <= lineTop || seg.Bounds.Top >= lineBottom)
                    continue;

                float clampedY1 = Math.Clamp(seg.P0.Y, lineTop, lineBottom);
                float clampedY2 = Math.Clamp(seg.P1.Y, lineTop, lineBottom);
                float signedDy = clampedY2 - clampedY1;
                if (MathF.Abs(signedDy) < 1e-5f) continue;

                areaDeltas[0] += signedDy;
                coverageDeltas[0] += signedDy;
            }

            for (int s = 0; s < tileSegs.Length; s++)
            {
                ref readonly PathSegment seg = ref segments[tileSegs[s]];
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
                    ColorBlend.BlendPixel(ref row[x], color, alpha);
                }
            }
        }
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
                        ColorBlend.BlendPixel(ref row[x], color, 1.0f);
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
                        ColorBlend.BlendPixel(ref row[x], color, alpha);
                    }
                }
            }
        });
    }
}
