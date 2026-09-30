namespace Glacier.Graphics.Tests;

using System;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class LcdRasterizerTests
{
    [Fact]
    public void LcdRasterizer_ProducesSubpixelBitmap()
    {
        var path = new VectorPath();
        path.AddRect(10, 10, 50, 50);

        LcdGlyphBitmap bitmap = LcdGlyphRasterizer.Rasterize(path, scale: 1.0f);

        Assert.True(bitmap.Width > 0);
        Assert.True(bitmap.Height > 0);
        Assert.Equal(bitmap.Width * 3, bitmap.Stride);
        Assert.Equal(bitmap.Stride * bitmap.Height, bitmap.Data.Length);
    }

    [Fact]
    public void GlyphAtlas_AddsAndRetrievesGlyph()
    {
        var atlas = new GlyphAtlas(512, 512);
        var font = new Font(size: 16f);

        AtlasGlyph g1 = atlas.GetOrAdd(font, 1, 0f);
        Assert.NotNull(g1);
        Assert.True(g1.Advance > 0f);

        // Cache hit
        AtlasGlyph g2 = atlas.GetOrAdd(font, 1, 0f);
        Assert.Same(g1, g2);
    }
}
