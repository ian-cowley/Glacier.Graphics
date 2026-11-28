namespace Glacier.Graphics.Text;

using System;
using System.Buffers;
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
/// Dynamic R8 grayscale shelf-packing texture atlas maintaining a value-keyed LRU cache
/// of rasterized font glyphs with warm-glyph compaction on capacity eviction.
/// </summary>
public sealed class GlyphAtlas
{
    private readonly record struct GlyphCacheKey(
        TrueTypeFont? Typeface,
        string FamilyName,
        float Size,
        bool Bold,
        bool Italic,
        int GlyphIndex,
        int SubpixelBin);

    private sealed class CacheEntry
    {
        public AtlasGlyph Glyph;
        public readonly LinkedListNode<GlyphCacheKey> LruNode;

        public CacheEntry(AtlasGlyph glyph, LinkedListNode<GlyphCacheKey> lruNode)
        {
            Glyph = glyph;
            LruNode = lruNode;
        }
    }

    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _pixels; // R8 coverage buffer

    private int _currentX;
    private int _currentY;
    private int _shelfHeight;

    private readonly Dictionary<GlyphCacheKey, CacheEntry> _cache = new();
    private readonly LinkedList<GlyphCacheKey> _lruOrder = new();

    public int Width => _width;
    public int Height => _height;
    public int CachedGlyphCount => _cache.Count;
    public ReadOnlySpan<byte> Pixels => _pixels;

    public GlyphAtlas(int width = 2048, int height = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _width = width;
        _height = height;
        _pixels = new byte[checked(width * height)];
        _currentX = 0;
        _currentY = 0;
        _shelfHeight = 0;
    }

    public void Clear()
    {
        Array.Clear(_pixels);
        _cache.Clear();
        _lruOrder.Clear();
        _currentX = 0;
        _currentY = 0;
        _shelfHeight = 0;
    }

    public AtlasGlyph GetOrAdd(Font font, int glyphIndex, float fractionalX = 0f)
    {
        ArgumentNullException.ThrowIfNull(font);
        int subpixelBin = Math.Clamp((int)MathF.Round(fractionalX * 4f), 0, 3);
        var typeface = font.EffectiveTypeface;
        var key = new GlyphCacheKey(typeface, font.FamilyName, font.Size, font.Bold, font.Italic, glyphIndex, subpixelBin);

        if (_cache.TryGetValue(key, out var entry))
        {
            _lruOrder.Remove(entry.LruNode);
            _lruOrder.AddFirst(entry.LruNode);
            return entry.Glyph;
        }

        // Render glyph
        float scale = typeface != null ? font.Size / typeface.UnitsPerEm : font.Size / 1000f;
        VectorPath glyphPath = typeface?.GetGlyphPath(glyphIndex) ?? new VectorPath();
        ushort adv = typeface?.GetAdvanceWidth(glyphIndex) ?? (ushort)1000;
        if (adv == 0 && (typeface == null || glyphPath.VerbCount == 0))
        {
            adv = (ushort)(typeface?.UnitsPerEm ?? 1000);
        }
        float advance = (adv / (float)(typeface?.UnitsPerEm ?? 1000)) * font.Size;

        LcdGlyphBitmap bitmap = LcdGlyphRasterizer.Rasterize(glyphPath, scale, subpixelBin * 0.25f);

        int bmpWidth = bitmap.Width;
        int bmpHeight = bitmap.Height;

        if (bmpWidth <= 0 || bmpHeight <= 0)
        {
            var emptyGlyph = new AtlasGlyph(RectF.Empty, 0f, 0f, advance);
            var node = _lruOrder.AddFirst(key);
            _cache[key] = new CacheEntry(emptyGlyph, node);
            return emptyGlyph;
        }

        // Shelf packing logic (add 1 pixel padding on all sides)
        int padWidth = bmpWidth + 2;
        int padHeight = bmpHeight + 2;

        if (padWidth > _width || padHeight > _height)
        {
            var oversizedEmpty = new AtlasGlyph(RectF.Empty, bitmap.BearingX, bitmap.BearingY, advance);
            var node = _lruOrder.AddFirst(key);
            _cache[key] = new CacheEntry(oversizedEmpty, node);
            return oversizedEmpty;
        }

        if (_currentX + padWidth > _width)
        {
            _currentX = 0;
            _currentY += _shelfHeight;
            _shelfHeight = 0;
        }

        if (_currentY + padHeight > _height)
        {
            EvictColdAndRepackWarm(requiredWidth: padWidth, requiredHeight: padHeight);
        }

        int targetX = _currentX + 1;
        int targetY = _currentY + 1;

        // Blit glyph R8 coverage (green channel represents central luminance in LCD RGB subpixels)
        for (int y = 0; y < bmpHeight; y++)
        {
            int srcRow = y * bitmap.Stride;
            int dstRow = (targetY + y) * _width + targetX;

            for (int x = 0; x < bmpWidth; x++)
            {
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

        var lruNode = _lruOrder.AddFirst(key);
        _cache[key] = new CacheEntry(glyph, lruNode);
        return glyph;
    }

    private void EvictColdAndRepackWarm(int requiredWidth, int requiredHeight)
    {
        byte[] tempPixels = ArrayPool<byte>.Shared.Rent(_pixels.Length);
        try
        {
            Array.Clear(tempPixels, 0, _pixels.Length);

            int newX = 0;
            int newY = 0;
            int newShelfH = 0;
            int maxWarmHeight = Math.Max(0, _height - requiredHeight);
            // Reserve at least 35% of atlas height (plus space for the incoming glyph) so thrashing does not immediately recur
            int warmBudgetY = Math.Min(maxWarmHeight, (int)(_height * 0.65f));

            var node = _lruOrder.First;
            bool keeping = true;

            while (node != null)
            {
                var next = node.Next;
                var entry = _cache[node.Value];
                RectF r = entry.Glyph.Rect;
                int gw = (int)r.Width;
                int gh = (int)r.Height;

                if (gw <= 0 || gh <= 0)
                {
                    // Empty glyphs occupy zero atlas pixels; keep warm ones or evict with cold tail
                    if (!keeping)
                    {
                        _cache.Remove(node.Value);
                        _lruOrder.Remove(node);
                    }
                    node = next;
                    continue;
                }

                if (keeping)
                {
                    int padW = gw + 2;
                    int padH = gh + 2;
                    int tryX = newX;
                    int tryY = newY;
                    int tryShelfH = newShelfH;

                    if (tryX + padW > _width)
                    {
                        tryX = 0;
                        tryY += tryShelfH;
                        tryShelfH = 0;
                    }

                    bool fitsWarmBudget = tryY + padH <= warmBudgetY;
                    bool leavesRoomForIncoming = (tryX + padW + requiredWidth <= _width && tryY + Math.Max(tryShelfH, padH) <= _height)
                        || (tryY + Math.Max(tryShelfH, padH) + requiredHeight <= _height);

                    if (fitsWarmBudget && leavesRoomForIncoming)
                    {
                        int dstX = tryX + 1;
                        int dstY = tryY + 1;
                        int oldX = (int)r.X;
                        int oldY = (int)r.Y;

                        for (int row = 0; row < gh; row++)
                        {
                            Buffer.BlockCopy(_pixels, (oldY + row) * _width + oldX, tempPixels, (dstY + row) * _width + dstX, gw);
                        }

                        entry.Glyph = new AtlasGlyph(new RectF(dstX, dstY, gw, gh), entry.Glyph.BearingX, entry.Glyph.BearingY, entry.Glyph.Advance);
                        newX = tryX + padW;
                        newY = tryY;
                        newShelfH = Math.Max(tryShelfH, padH);
                        node = next;
                        continue;
                    }

                    keeping = false;
                }

                _cache.Remove(node.Value);
                _lruOrder.Remove(node);
                node = next;
            }

            tempPixels.AsSpan(0, _pixels.Length).CopyTo(_pixels);
            _currentX = newX;
            _currentY = newY;
            _shelfHeight = newShelfH;

            if (_currentX + requiredWidth > _width)
            {
                _currentX = 0;
                _currentY += _shelfHeight;
                _shelfHeight = 0;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(tempPixels);
        }
    }
}
