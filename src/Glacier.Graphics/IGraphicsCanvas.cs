namespace Glacier.Graphics;

using System;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;

/// <summary>
/// Primary high-level 2D graphics canvas abstraction.
/// Eliminates SkiaSharp P/Invoke overhead with 100% pure managed C# .NET 10 execution.
/// </summary>
public interface IGraphicsCanvas : IDisposable
{
    /// <summary>Canvas width in pixels.</summary>
    int Width { get; }

    /// <summary>Canvas height in pixels.</summary>
    int Height { get; }

    /// <summary>Clears the entire canvas with the specified color.</summary>
    void Clear(Rgba32 color);

    /// <summary>Draws (strokes) a vector path with the specified paint.</summary>
    void DrawPath(in VectorPath path, in Paint paint);

    /// <summary>Fills a vector path with the specified paint.</summary>
    void FillPath(in VectorPath path, in Paint paint);

    /// <summary>Draws text string at given position with font and paint.</summary>
    void DrawText(ReadOnlySpan<char> text, float x, float y, in Font font, in Paint paint);

    /// <summary>Draws an image from a 2D span of RGBA32 pixels to the target rectangle.</summary>
    void DrawImage(in ReadOnlySpan2D<Rgba32> image, float x, float y, float width, float height);

    /// <summary>Pushes current state (transform, clip) to the state stack.</summary>
    void Save();

    /// <summary>Restores state (transform, clip) from top of the state stack.</summary>
    void Restore();

    /// <summary>Intersects current clipping region with the given path.</summary>
    void ClipPath(in VectorPath path);

    /// <summary>Flushes pending drawing commands to destination.</summary>
    void Flush();
}
