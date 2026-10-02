namespace Glacier.Graphics;

using System;
using System.Collections.Generic;
using Glacier.Graphics.Raster;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;

/// <summary>
/// High-throughput software rasterization canvas implementing IGraphicsCanvas.
/// Backed by LinearFramebuffer, multi-core SIMD rasterizer, and dynamic font atlas.
/// </summary>
public sealed class CpuGraphicsCanvas : IGraphicsCanvas
{
    private readonly LinearFramebuffer _framebuffer;
    private readonly bool _ownsFramebuffer;
    private readonly CpuRasterizer _rasterizer = new();
    private readonly GlyphAtlas _atlas = new();

    private readonly Stack<CanvasState> _stateStack = new();
    private CanvasState _currentState;
    private bool _disposed;

    private struct CanvasState
    {
        public Matrix3x2 Transform;
        public RectF Clip;
    }

    public int Width => _framebuffer.Width;
    public int Height => _framebuffer.Height;
    public LinearFramebuffer Framebuffer => _framebuffer;

    public CpuGraphicsCanvas(int width, int height)
    {
        _framebuffer = new LinearFramebuffer(width, height);
        _ownsFramebuffer = true;
        _currentState = new CanvasState
        {
            Transform = Matrix3x2.Identity,
            Clip = RectF.FromLTRB(0, 0, width, height)
        };
    }

    public CpuGraphicsCanvas(LinearFramebuffer framebuffer)
    {
        _framebuffer = framebuffer ?? throw new ArgumentNullException(nameof(framebuffer));
        _ownsFramebuffer = false;
        _currentState = new CanvasState
        {
            Transform = Matrix3x2.Identity,
            Clip = RectF.FromLTRB(0, 0, framebuffer.Width, framebuffer.Height)
        };
    }

    public void Clear(Rgba32 color)
    {
        _framebuffer.Clear(color);
    }

    public void DrawPath(in VectorPath path, in Paint paint)
    {
        if (path == null || path.VerbCount == 0 || paint.Color.A == 0) return;

        var transformed = new VectorPath(path);
        if (_currentState.Transform != Matrix3x2.Identity)
        {
            transformed.Transform(_currentState.Transform);
        }

        VectorPath stroked = LineStroker.Stroke(
            transformed,
            paint.StrokeWidth,
            paint.Cap,
            paint.Join,
            paint.MiterLimit);

        // Use EvenOdd winding so self-intersecting stroke polygons (e.g. fast-oscillating
        // signal lines) render as thin strokes rather than solid filled regions.
        _rasterizer.FillPath(_framebuffer, stroked, paint.Color, WindingRule.EvenOdd, _currentState.Clip);
    }

    public void FillPath(in VectorPath path, in Paint paint)
    {
        if (path == null || path.VerbCount == 0 || paint.Color.A == 0) return;

        var transformed = new VectorPath(path);
        if (_currentState.Transform != Matrix3x2.Identity)
        {
            transformed.Transform(_currentState.Transform);
        }

        _rasterizer.FillPath(_framebuffer, transformed, paint.Color, transformed.FillRule, _currentState.Clip);
    }

    public void DrawText(ReadOnlySpan<char> text, float x, float y, in Font font, in Paint paint)
    {
        if (text.IsEmpty || paint.Color.A == 0) return;

        float curX = x;
        for (int i = 0; i < text.Length; i++)
        {
            int codePoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? char.ConvertToUtf32(text[i++], text[i])
                : text[i];

            int glyphIdx = font.EffectiveTypeface?.GetGlyphIndex(codePoint) ?? 0;
            float fracX = curX - MathF.Floor(curX);

            AtlasGlyph glyph = _atlas.GetOrAdd(font, glyphIdx, fracX);

            if (glyph.Rect.Width > 0 && glyph.Rect.Height > 0)
            {
                int dstX = (int)MathF.Round(curX + glyph.BearingX);
                int dstY = (int)MathF.Round(y + glyph.BearingY);
                int gWidth = (int)glyph.Rect.Width;
                int gHeight = (int)glyph.Rect.Height;
                int srcX = (int)glyph.Rect.X;
                int srcY = (int)glyph.Rect.Y;

                ReadOnlySpan<byte> atlasPixels = _atlas.Pixels;
                int atlasWidth = _atlas.Width;

                for (int gy = 0; gy < gHeight; gy++)
                {
                    int py = dstY + gy;
                    if (py < _currentState.Clip.Top || py >= _currentState.Clip.Bottom || py >= _framebuffer.Height)
                        continue;

                    var row = _framebuffer.GetRowSpan(py);
                    int atlasRow = (srcY + gy) * atlasWidth + srcX;

                    for (int gx = 0; gx < gWidth; gx++)
                    {
                        int px = dstX + gx;
                        if (px < _currentState.Clip.Left || px >= _currentState.Clip.Right || px >= _framebuffer.Width)
                            continue;

                        byte coverage = atlasPixels[atlasRow + gx];
                        if (coverage > 0)
                        {
                            float alpha = (coverage / 255.0f) * (paint.Color.A / 255.0f);
                            BlendPixel(ref row[px], paint.Color, alpha);
                        }
                    }
                }
            }

            curX += glyph.Advance;
        }
    }

    public void DrawImage(in ReadOnlySpan2D<Rgba32> image, float x, float y, float width, float height)
    {
        if (image.Width <= 0 || image.Height <= 0 || width <= 0f || height <= 0f) return;

        int dstMinX = Math.Max((int)MathF.Floor(x), (int)MathF.Floor(_currentState.Clip.Left));
        int dstMaxX = Math.Min((int)MathF.Ceiling(x + width), (int)MathF.Ceiling(_currentState.Clip.Right));
        int dstMinY = Math.Max((int)MathF.Floor(y), (int)MathF.Floor(_currentState.Clip.Top));
        int dstMaxY = Math.Min((int)MathF.Ceiling(y + height), (int)MathF.Ceiling(_currentState.Clip.Bottom));

        float scaleX = image.Width / width;
        float scaleY = image.Height / height;

        for (int py = dstMinY; py < dstMaxY; py++)
        {
            if (py >= _framebuffer.Height) break;
            var row = _framebuffer.GetRowSpan(py);

            int srcY = Math.Clamp((int)((py - y) * scaleY), 0, image.Height - 1);
            var srcRow = image.GetRowSpan(srcY);

            for (int px = dstMinX; px < dstMaxX; px++)
            {
                if (px >= _framebuffer.Width) break;
                int srcX = Math.Clamp((int)((px - x) * scaleX), 0, image.Width - 1);
                Rgba32 srcColor = srcRow[srcX];
                if (srcColor.A == 255)
                {
                    row[px] = srcColor;
                }
                else if (srcColor.A > 0)
                {
                    BlendPixel(ref row[px], srcColor, srcColor.A / 255.0f);
                }
            }
        }
    }

    public void Save()
    {
        _stateStack.Push(_currentState);
    }

    public void Restore()
    {
        if (_stateStack.Count > 0)
        {
            _currentState = _stateStack.Pop();
        }
    }

    public void ClipPath(in VectorPath path)
    {
        if (path == null || path.PointCount == 0) return;
        RectF pathBounds = path.ComputeBounds();
        _currentState.Clip = _currentState.Clip.Intersect(pathBounds);
    }

    public void Flush() { }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_ownsFramebuffer)
            {
                _framebuffer.Dispose();
            }
            _disposed = true;
        }
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
