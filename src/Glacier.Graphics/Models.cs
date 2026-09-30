namespace Glacier.Graphics;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// 32-bit RGBA color (8 bits per channel: Red, Green, Blue, Alpha).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Rgba32(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba32 Transparent = new(0, 0, 0, 0);
    public static readonly Rgba32 Black = new(0, 0, 0, 255);
    public static readonly Rgba32 White = new(255, 255, 255, 255);
    public static readonly Rgba32 Red = new(255, 0, 0, 255);
    public static readonly Rgba32 Green = new(0, 255, 0, 255);
    public static readonly Rgba32 Blue = new(0, 0, 255, 255);
    public static readonly Rgba32 Yellow = new(255, 255, 0, 255);
    public static readonly Rgba32 Cyan = new(0, 255, 255, 255);
    public static readonly Rgba32 Magenta = new(255, 0, 255, 255);
    public static readonly Rgba32 Gray = new(128, 128, 128, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba32 FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba32 FromRgba(byte r, byte g, byte b, byte a) => new(r, g, b, a);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba32 FromFloat(float r, float g, float b, float a = 1.0f)
    {
        return new Rgba32(
            (byte)Math.Clamp((int)MathF.Round(r * 255.0f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(g * 255.0f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(b * 255.0f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(a * 255.0f), 0, 255));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Bgra32 ToBgra32() => new(B, G, R, A);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ToRgbaUint() => (uint)(R | (G << 8) | (B << 16) | (A << 24));
}

/// <summary>
/// 32-bit BGRA color (8 bits per channel: Blue, Green, Red, Alpha).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Bgra32(byte B, byte G, byte R, byte A = 255)
{
    public static readonly Bgra32 Transparent = new(0, 0, 0, 0);
    public static readonly Bgra32 Black = new(0, 0, 0, 255);
    public static readonly Bgra32 White = new(255, 255, 255, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Rgba32 ToRgba32() => new(R, G, B, A);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ToBgraUint() => (uint)(B | (G << 8) | (R << 16) | (A << 24));
}

/// <summary>
/// 2D floating-point point.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct PointF(float X, float Y)
{
    public static readonly PointF Zero = new(0f, 0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PointF operator +(PointF a, PointF b) => new(a.X + b.X, a.Y + b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PointF operator -(PointF a, PointF b) => new(a.X - b.X, a.Y - b.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PointF operator *(PointF a, float s) => new(a.X * s, a.Y * s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PointF operator /(PointF a, float s) => new(a.X / s, a.Y / s);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float LengthSquared() => X * X + Y * Y;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Length() => MathF.Sqrt(LengthSquared());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PointF Normalize()
    {
        float len = Length();
        return len > 1e-6f ? this / len : Zero;
    }
}

/// <summary>
/// 2D floating-point rectangle.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public static readonly RectF Empty = new(0f, 0f, 0f, 0f);

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RectF FromLTRB(float left, float top, float right, float bottom)
        => new(left, top, MathF.Max(0f, right - left), MathF.Max(0f, bottom - top));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(float x, float y)
        => x >= Left && x <= Right && y >= Top && y <= Bottom;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IntersectsWith(in RectF other)
        => !(other.Left > Right || other.Right < Left || other.Top > Bottom || other.Bottom < Top);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RectF Intersect(in RectF other)
    {
        float l = MathF.Max(Left, other.Left);
        float t = MathF.Max(Top, other.Top);
        float r = MathF.Min(Right, other.Right);
        float b = MathF.Min(Bottom, other.Bottom);
        if (r < l || b < t) return Empty;
        return FromLTRB(l, t, r, b);
    }
}

/// <summary>
/// Affine 2D transformation matrix.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Matrix3x2(
    float M11, float M12,
    float M21, float M22,
    float M31, float M32)
{
    public static readonly Matrix3x2 Identity = new(1f, 0f, 0f, 1f, 0f, 0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix3x2 CreateTranslation(float x, float y)
        => new(1f, 0f, 0f, 1f, x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix3x2 CreateScale(float sx, float sy)
        => new(sx, 0f, 0f, sy, 0f, 0f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix3x2 CreateRotation(float radians)
    {
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        return new Matrix3x2(c, s, -s, c, 0f, 0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Matrix3x2 Multiply(in Matrix3x2 a, in Matrix3x2 b)
    {
        return new Matrix3x2(
            a.M11 * b.M11 + a.M12 * b.M21,
            a.M11 * b.M12 + a.M12 * b.M22,
            a.M21 * b.M11 + a.M22 * b.M21,
            a.M21 * b.M12 + a.M22 * b.M22,
            a.M31 * b.M11 + a.M32 * b.M21 + b.M31,
            a.M31 * b.M12 + a.M32 * b.M22 + b.M32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PointF TransformPoint(in Matrix3x2 m, PointF p)
    {
        return new PointF(
            p.X * m.M11 + p.Y * m.M21 + m.M31,
            p.X * m.M12 + p.Y * m.M22 + m.M32);
    }
}

/// <summary>
/// A non-allocating, lightweight 2D span view over linear pixel memory.
/// </summary>
public readonly ref struct ReadOnlySpan2D<T>
{
    private readonly ReadOnlySpan<T> _data;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public ReadOnlySpan2D(ReadOnlySpan<T> data, int width, int height, int stride)
    {
        if (width < 0 || height < 0 || stride < width)
            throw new ArgumentOutOfRangeException();
        _data = data;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public ReadOnlySpan<T> GetRowSpan(int y)
    {
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return _data.Slice(y * Stride, Width);
    }

    public ref readonly T this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException();
            return ref _data[y * Stride + x];
        }
    }
}

/// <summary>
/// Winding fill rule for vector paths.
/// </summary>
public enum WindingRule : byte
{
    NonZero,
    EvenOdd
}

/// <summary>
/// Line cap styles.
/// </summary>
public enum StrokeCap : byte
{
    Butt,
    Square,
    Round
}

/// <summary>
/// Line join styles.
/// </summary>
public enum StrokeJoin : byte
{
    Miter,
    Round,
    Bevel
}

/// <summary>
/// Paint styles.
/// </summary>
public enum PaintStyle : byte
{
    Fill,
    Stroke,
    StrokeAndFill
}

/// <summary>
/// Drawing paint description.
/// </summary>
public readonly record struct Paint(
    Rgba32 Color,
    PaintStyle Style = PaintStyle.Fill,
    float StrokeWidth = 1.0f,
    StrokeJoin Join = StrokeJoin.Miter,
    StrokeCap Cap = StrokeCap.Butt,
    bool AntiAlias = true,
    float MiterLimit = 4.0f);
