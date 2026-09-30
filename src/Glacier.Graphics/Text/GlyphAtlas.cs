namespace Glacier.Graphics.Text;

using System;
using System.Collections.Generic;
using Glacier.Graphics.Vector;

/// <summary>
/// Cached glyph metrics and UV texture quad in the atlas.
/// </summary>
public sealed class AtlasGlyph
{
    public RectF Rect { get; }
    public float BearingX { get; }
    public float BearingY { get; }
    public float Advance { get; }

    public AtlasGlyph(RectF rect, float bearingX, float bearingY, float advance)
    {
        Rect = rect;
        BearingX = bearingX;
        BearingY = bearingY;
        Advance = advance;
    }
}

/// <summary>
/// Dynamic texture atlas using shelf-packing allocation and LRU caching
/// for rasterized font glyphs.
/// </summary>
public sealed class GlyphAtlas
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixels; // R8 coverage buffer

    private int _currentX;
    private int _currentY;
    private int _shelfHeight;

    private readonly Dictionary<(Font Font, int GlyphIndex, int SubpixelBin), AtlasGlyph> _cache = new();

    public int Width => _width;
    public int Height => _height;
    public ReadOnlySpan<byte> Pixels => _pixels;

    public GlyphAtlas(int width = 2048, int height = 2048)
    {
        _width = width;
        _height = height;
        _pixels = new byte[width * height];
        _currentX = 0;
        _currentY = 0;
        _shelfHeight = 0;
    }

    public void Clear()
    {
        Array.Clear(_pixels);
        _cache.Clear();
        _currentX = 0;
        _currentY = 0;
        _shelfHeight = 0;
    }

    public AtlasGlyph GetOrAdd(Font font, int glyphIndex, float fractionalX = 0f)
    {
        int subpixelBin = Math.Clamp((int)MathF.Round(fractionalX * 4f), 0, 3);
        var key = (font, glyphIndex, subpixelBin);

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Render glyph
        float scale = font.Typeface != null ? font.Size / font.Typeface.UnitsPerEm : font.Size / 1000f;
        VectorPath glyphPath = font.Typeface?.GetGlyphPath(glyphIndex) ?? new VectorPath();
        ushort adv = font.Typeface?.GetAdvanceWidth(glyphIndex) ?? (ushort)1000;
        float advance = (adv / (float)(font.Typeface?.UnitsPerEm ?? 1000)) * font.Size;

        LcdGlyphBitmap bitmap = LcdGlyphRasterizer.Rasterize(glyphPath, scale, subpixelBin * 0.25f);

        // Pack into shelf
        int bmpWidth = bitmap.Width;
        int bmpHeight = bitmap.Height;

        if (bmpWidth <= 0 || bmpHeight <= 0)
        {
            var emptyGlyph = new AtlasGlyph(RectF.Empty, 0f, 0f, advance);
            _cache[key] = emptyGlyph;
            return emptyGlyph;
        }

        // Shelf packing logic (add 1 pixel padding)
        int padWidth = bmpWidth + 2;
        int padHeight = bmpHeight + 2;

        if (_currentX + padWidth > _width)
        {
            // Move to next shelf
            _currentX = 0;
            _currentY += _shelfHeight;
            _shelfHeight = 0;
        }

        if (_currentY + padHeight > _height)
        {
            // Atlas is full -> evict and reset
            Clear();
            _currentX = 0;
            _currentY = 0;
            _shelfHeight = 0;
        }

        int targetX = _currentX + 1;
        int targetY = _currentY + 1;

        // Blit glyph R8 coverage (using green channel of LCD bitmap or average)
        for (int y = 0; y < bmpHeight; y++)
        {
            int srcRow = y * bitmap.Stride;
            int dstRow = (targetY + y) * _width + targetX;

            for (int x = 0; x < bmpWidth; x++)
            {
                // Green channel represents central luminance in LCD RGB subpixels
                _pixels[dstRow + x] = bitmap.Data[srcRow + x * 3 + 1];
            }
        }

        _currentX += padWidth;
        _shelfHeight = Math.Max(_shelfHeight, padHeight);

        var glyph = new AtlasGlyph(
            new RectF(targetX, targetY, bmpWidth, bmpHeight),
            bitmap.BearingX,
            bitmap.BearingY,
            advance);

        _cache[key] = glyph;
        return glyph;
    }
}
