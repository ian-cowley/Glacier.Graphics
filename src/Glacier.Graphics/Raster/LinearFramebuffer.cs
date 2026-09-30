namespace Glacier.Graphics.Raster;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Contiguous unmanaged linear framebuffer for hardware and software rasterization.
/// Supports both RGBA32 and BGRA32 color pixel layouts with zero heap allocation overhead.
/// </summary>
public sealed unsafe class LinearFramebuffer : IDisposable
{
    private void* _memory;
    private readonly bool _ownsMemory;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; } // Bytes per row (Width * 4)

    public LinearFramebuffer(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException("Width and Height must be positive.");

        Width = width;
        Height = height;
        Stride = width * sizeof(Rgba32);
        nuint totalBytes = (nuint)(Stride * height);

        _memory = NativeMemory.AllocZeroed(totalBytes);
        _ownsMemory = true;
    }

    public LinearFramebuffer(void* memory, int width, int height, int stride)
    {
        _memory = memory;
        Width = width;
        Height = height;
        Stride = stride;
        _ownsMemory = false;
    }

    ~LinearFramebuffer()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (_ownsMemory && _memory != null)
            {
                NativeMemory.Free(_memory);
                _memory = null;
            }
            _disposed = true;
        }
    }

    private void CheckDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LinearFramebuffer));
    }

    public Span<Rgba32> AsRgbaSpan()
    {
        CheckDisposed();
        return new Span<Rgba32>(_memory, Width * Height);
    }

    public Span<Bgra32> AsBgraSpan()
    {
        CheckDisposed();
        return new Span<Bgra32>(_memory, Width * Height);
    }

    public Span<byte> AsByteSpan()
    {
        CheckDisposed();
        return new Span<byte>(_memory, Stride * Height);
    }

    public Span<Rgba32> GetRowSpan(int y)
    {
        CheckDisposed();
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        byte* rowPtr = (byte*)_memory + (y * Stride);
        return new Span<Rgba32>(rowPtr, Width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Rgba32 GetPixel(int x, int y)
    {
        CheckDisposed();
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException();
        byte* row = (byte*)_memory + (y * Stride);
        return ((Rgba32*)row)[x];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, Rgba32 color)
    {
        CheckDisposed();
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        byte* row = (byte*)_memory + (y * Stride);
        ((Rgba32*)row)[x] = color;
    }

    public void Clear(Rgba32 color)
    {
        CheckDisposed();
        var span = AsRgbaSpan();
        span.Fill(color);
    }

    public ReadOnlySpan2D<Rgba32> AsReadOnlySpan2D()
    {
        CheckDisposed();
        return new ReadOnlySpan2D<Rgba32>(AsRgbaSpan(), Width, Height, Width);
    }
}
