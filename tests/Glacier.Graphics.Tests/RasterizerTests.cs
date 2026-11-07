namespace Glacier.Graphics.Tests;

using System;
using Glacier.Graphics;
using Glacier.Graphics.Raster;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class RasterizerTests
{
    [Fact]
    public void LinearFramebuffer_AllocatesAndAccessesPixels()
    {
        using var fb = new LinearFramebuffer(100, 50);
        Assert.Equal(100, fb.Width);
        Assert.Equal(50, fb.Height);
        Assert.Equal(100 * 4, fb.Stride);

        fb.Clear(Rgba32.Red);
        Assert.Equal(Rgba32.Red, fb.GetPixel(0, 0));
        Assert.Equal(Rgba32.Red, fb.GetPixel(99, 49));

        fb.SetPixel(50, 25, Rgba32.Blue);
        Assert.Equal(Rgba32.Blue, fb.GetPixel(50, 25));
    }

    [Fact]
    public void AnalyticalCoverage_ComputesTrapezoidArea()
    {
        // Line from (0, 0) to (1, 1) across unit cell [0, 1]x[0, 1]
        // Area under diagonal should be 0.5
        float area = AnalyticalCoverage.ComputeTrapezoidSignedArea(0f, 0f, 1f, 1f);
        Assert.Equal(0.5f, area, precision: 5);
    }

    [Fact]
    public void CpuRasterizer_FillsRectanglePath()
    {
        using var fb = new LinearFramebuffer(100, 100);
        var rasterizer = new CpuRasterizer();

        var path = new VectorPath();
        path.AddRect(20, 20, 60, 60);

        rasterizer.FillPath(fb, path, Rgba32.Green);

        // Pixel inside should be green
        Rgba32 inside = fb.GetPixel(50, 50);
        Assert.True(inside.G > 200);

        // Pixel outside should be transparent black (0, 0, 0, 0)
        Rgba32 outside = fb.GetPixel(5, 5);
        Assert.Equal(0, outside.A);
    }

    [Fact]
    public void TileComputeRasterizer_BinsAndRasterizes()
    {
        using var fb = new LinearFramebuffer(64, 64);
        var tileRasterizer = new TileComputeRasterizer();

        var path = new VectorPath();
        path.AddRect(16, 16, 32, 32);

        tileRasterizer.Rasterize(fb, path, Rgba32.Blue);

        // Center pixel inside should be blue
        Rgba32 center = fb.GetPixel(32, 32);
        Assert.True(center.B > 200);

        // Corner pixel outside should be transparent
        Rgba32 corner = fb.GetPixel(4, 4);
        Assert.Equal(0, corner.A);
    }

    [Fact]
    public void TileComputeRasterizer_RasterizesCwAndCcwMultiTileTrianglesWithoutRightEdgeLeak()
    {
        var tileRasterizer = new TileComputeRasterizer();

        // Clockwise triangle spanning 3x3 tiles (x: 8..52, y: 8..52) without explicit duplicate closing vertex
        using (var fbCw = new LinearFramebuffer(64, 64))
        {
            var cwTri = new VectorPath();
            cwTri.MoveTo(30, 8);
            cwTri.LineTo(52, 52);
            cwTri.LineTo(8, 52);
            cwTri.Close();

            tileRasterizer.Rasterize(fbCw, cwTri, Rgba32.Red);

            // Interior centroid (30, 38) must be filled red
            Assert.True(fbCw.GetPixel(30, 38).R > 200);
            Assert.Equal(255, fbCw.GetPixel(30, 38).A);

            // Exterior pixels strictly to the right and left of the triangle must remain transparent
            Assert.Equal(0, fbCw.GetPixel(60, 38).A);
            Assert.Equal(0, fbCw.GetPixel(2, 38).A);
        }

        // Counter-clockwise triangle plus negative-X left-clipped rectangle
        using (var fbCcw = new LinearFramebuffer(64, 64))
        {
            var ccwTri = new VectorPath();
            ccwTri.MoveTo(30, 8);
            ccwTri.LineTo(8, 52);
            ccwTri.LineTo(52, 52);
            ccwTri.Close();

            tileRasterizer.Rasterize(fbCcw, ccwTri, Rgba32.Green);
            Assert.True(fbCcw.GetPixel(30, 38).G > 200);
            Assert.Equal(0, fbCcw.GetPixel(60, 38).A);

            var negRect = new VectorPath();
            negRect.AddRect(-10, 10, 30, 20); // spans x = -10..20
            tileRasterizer.Rasterize(fbCcw, negRect, Rgba32.Blue);
            Assert.True(fbCcw.GetPixel(5, 15).B > 200);
            Assert.Equal(0, fbCcw.GetPixel(28, 15).B);
        }
    }
}
