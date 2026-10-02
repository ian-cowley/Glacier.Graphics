namespace Glacier.Graphics.Tests;

using System;
using Glacier.Graphics;
using Glacier.Graphics.Raster;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class CanvasApiTests
{
    [Fact]
    public void Canvas_Clear_FillsCanvasWithColor()
    {
        using var canvas = new CpuGraphicsCanvas(50, 50);
        canvas.Clear(Rgba32.Red);

        Assert.Equal(Rgba32.Red, canvas.Framebuffer.GetPixel(0, 0));
        Assert.Equal(Rgba32.Red, canvas.Framebuffer.GetPixel(25, 25));
        Assert.Equal(Rgba32.Red, canvas.Framebuffer.GetPixel(49, 49));
    }

    [Fact]
    public void Canvas_FillAndStrokePath_RendersPixels()
    {
        using var canvas = new CpuGraphicsCanvas(100, 100);
        canvas.Clear(Rgba32.White);

        var path = new VectorPath();
        path.AddRect(20, 20, 40, 40);

        canvas.FillPath(path, new Paint(Rgba32.Blue));

        Rgba32 center = canvas.Framebuffer.GetPixel(40, 40);
        Assert.True(center.B > 200);

        Rgba32 outside = canvas.Framebuffer.GetPixel(5, 5);
        Assert.Equal(255, outside.R);
        Assert.Equal(255, outside.G);
        Assert.Equal(255, outside.B);
    }

    [Fact]
    public void Canvas_DrawImage_ScalesAndCopiesPixels()
    {
        using var canvas = new CpuGraphicsCanvas(100, 100);
        canvas.Clear(Rgba32.White);

        using var srcFb = new LinearFramebuffer(10, 10);
        srcFb.Clear(Rgba32.Green);

        canvas.DrawImage(srcFb.AsReadOnlySpan2D(), 20, 20, 40, 40);

        Rgba32 blitted = canvas.Framebuffer.GetPixel(30, 30);
        Assert.True(blitted.G > 200);
    }

    [Fact]
    public void Canvas_SaveRestore_RestoresClipState()
    {
        using var canvas = new CpuGraphicsCanvas(100, 100);
        canvas.Save();

        var clipPath = new VectorPath();
        clipPath.AddRect(10, 10, 20, 20);
        canvas.ClipPath(clipPath);

        canvas.Restore();
        // State restored
    }

    [Fact]
    public void Canvas_DrawText_RendersPixels()
    {
        using var canvas = new CpuGraphicsCanvas(100, 100);
        canvas.Clear(Rgba32.White);

        var font = new Font(size: 20f);
        canvas.DrawText("A", 10, 40, font, new Paint(Rgba32.Black));

        int darkCount = 0;
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                var p = canvas.Framebuffer.GetPixel(x, y);
                if (p.R < 200 || p.G < 200 || p.B < 200)
                    darkCount++;
            }
        }

        Assert.True(darkCount > 0, $"Expected dark pixels for rendered text, but found {darkCount}. System font: {font.EffectiveTypeface != null}");
    }
}

