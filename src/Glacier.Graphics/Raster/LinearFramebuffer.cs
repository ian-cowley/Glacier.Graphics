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
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        Width = width;
        Height = height;
        Stride = checked(width * sizeof(Rgba32));
        nuint totalBytes = checked((nuint)(uint)Stride * (nuint)(uint)height);

        _memory = NativeMemory.AllocZeroed(totalBytes);
        _ownsMemory = true;
    }

    public LinearFramebuffer(void* memory, int width, int height, int stride)
    {
        if (memory == null)
            throw new ArgumentNullException(nameof(memory));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        int minStride = checked(width * sizeof(Rgba32));
        if (stride < minStride)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"Stride ({stride}) must be at least {minStride} bytes.");

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
        if (Stride != Width * sizeof(Rgba32))
            throw new InvalidOperationException("Framebuffer has padded rows (Stride > Width * sizeof(Rgba32)); use GetRowSpan(y) for row-by-row access.");
        long totalPixels = (long)(uint)Width * (uint)Height;
        if (totalPixels > int.MaxValue)
            throw new InvalidOperationException("Framebuffer pixel count exceeds Span<Rgba32> maximum length.");
        return new Span<Rgba32>(_memory, (int)totalPixels);
    }

    public Span<Bgra32> AsBgraSpan()
    {
        CheckDisposed();
        if (Stride != Width * sizeof(Bgra32))
            throw new InvalidOperationException("Framebuffer has padded rows (Stride > Width * sizeof(Bgra32)); use GetRowSpan(y) for row-by-row access.");
        long totalPixels = (long)(uint)Width * (uint)Height;
        if (totalPixels > int.MaxValue)
            throw new InvalidOperationException("Framebuffer pixel count exceeds Span<Bgra32> maximum length.");
        return new Span<Bgra32>(_memory, (int)totalPixels);
    }

    public Span<byte> AsByteSpan()
    {
        CheckDisposed();
        long totalBytes = (long)(uint)Stride * (uint)Height;
        if (totalBytes > int.MaxValue)
            throw new InvalidOperationException("Framebuffer byte size exceeds Span<byte> maximum length.");
        return new Span<byte>(_memory, (int)totalBytes);
    }

    public Span<Rgba32> GetRowSpan(int y)
    {
        CheckDisposed();
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        byte* rowPtr = (byte*)_memory + ((nuint)(uint)y * (nuint)(uint)Stride);
        return new Span<Rgba32>(rowPtr, Width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Rgba32 GetPixel(int x, int y)
    {
        CheckDisposed();
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException();
        byte* row = (byte*)_memory + ((nuint)(uint)y * (nuint)(uint)Stride);
        return ((Rgba32*)row)[x];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetPixel(int x, int y, Rgba32 color)
    {
        CheckDisposed();
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        byte* row = (byte*)_memory + ((nuint)(uint)y * (nuint)(uint)Stride);
        ((Rgba32*)row)[x] = color;
    }

    public void Clear(Rgba32 color)
    {
        CheckDisposed();
        if (Stride == Width * sizeof(Rgba32))
        {
            long totalPixels = (long)(uint)Width * (uint)Height;
            if (totalPixels <= int.MaxValue)
            {
                new Span<Rgba32>(_memory, (int)totalPixels).Fill(color);
                return;
            }
        }

        for (int y = 0; y < Height; y++)
        {
            GetRowSpan(y).Fill(color);
        }
    }

    public ReadOnlySpan2D<Rgba32> AsReadOnlySpan2D()
    {
        CheckDisposed();
        if (Stride % sizeof(Rgba32) != 0)
            throw new InvalidOperationException("Framebuffer Stride must be a multiple of sizeof(Rgba32) to create a ReadOnlySpan2D<Rgba32>.");
        int pixelStride = Stride / sizeof(Rgba32);
        long totalPixels = (long)(Height - 1) * pixelStride + Width;
        if (totalPixels > int.MaxValue)
            throw new InvalidOperationException("Framebuffer pixel span exceeds ReadOnlySpan<Rgba32> maximum length.");
        return new ReadOnlySpan2D<Rgba32>(new ReadOnlySpan<Rgba32>(_memory, (int)totalPixels), Width, Height, pixelStride);
    }
}
