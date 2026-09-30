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
}
