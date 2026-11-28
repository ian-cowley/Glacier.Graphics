namespace Glacier.Graphics.Pdf;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Pooled growable <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}.Shared"/>
/// that avoids intermediate contiguous array reallocations during PDF generation.
/// </summary>
internal class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultBlockSize = 8192;
    private readonly List<byte[]> _blocks = new();
    private readonly List<int> _blockLengths = new();
    private int _posInLast;
    private long _written;
    private bool _disposed;

    public int WrittenCount => checked((int)_written);
    public long WrittenLength => _written;

    public void Advance(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (_blocks.Count == 0 && count > 0)
            throw new InvalidOperationException("Cannot advance an empty buffer.");
        if (_blocks.Count > 0 && _posInLast + count > _blocks[^1].Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Advance exceeds current block capacity.");

        _posInLast += count;
        _written += count;
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int needed = Math.Max(1, sizeHint);
        if (_blocks.Count == 0 || (_blocks[^1].Length - _posInLast < needed))
        {
            if (_blocks.Count > 0)
            {
                _blockLengths[^1] = _posInLast;
            }
            int newSize = Math.Max(DefaultBlockSize, needed);
            byte[] buf = ArrayPool<byte>.Shared.Rent(newSize);
            _blocks.Add(buf);
            _blockLengths.Add(0);
            _posInLast = 0;
        }
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        byte[] last = _blocks[^1];
        return new Memory<byte>(last, _posInLast, last.Length - _posInLast);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        byte[] last = _blocks[^1];
        return new Span<byte>(last, _posInLast, last.Length - _posInLast);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            Span<byte> dst = GetSpan(Math.Min(data.Length, DefaultBlockSize));
            int toCopy = Math.Min(dst.Length, data.Length);
            data.Slice(0, toCopy).CopyTo(dst);
            Advance(toCopy);
            data = data.Slice(toCopy);
        }
    }

    public void WriteByte(byte value)
    {
        Span<byte> dst = GetSpan(1);
        dst[0] = value;
        Advance(1);
    }

    public void CopyTo(Stream dest)
    {
        ArgumentNullException.ThrowIfNull(dest);
        for (int i = 0; i < _blocks.Count; i++)
        {
            byte[] block = _blocks[i];
            int count = (i == _blocks.Count - 1) ? _posInLast : _blockLengths[i];
            if (count > 0) dest.Write(block, 0, count);
        }
    }

    public void CopyTo(IBufferWriter<byte> dest)
    {
        ArgumentNullException.ThrowIfNull(dest);
        for (int i = 0; i < _blocks.Count; i++)
        {
            byte[] block = _blocks[i];
            int count = (i == _blocks.Count - 1) ? _posInLast : _blockLengths[i];
            if (count > 0)
            {
                ReadOnlySpan<byte> src = block.AsSpan(0, count);
                while (!src.IsEmpty)
                {
                    Span<byte> dst = dest.GetSpan(Math.Min(src.Length, DefaultBlockSize));
                    int toCopy = Math.Min(dst.Length, src.Length);
                    src.Slice(0, toCopy).CopyTo(dst);
                    dest.Advance(toCopy);
                    src = src.Slice(toCopy);
                }
            }
        }
    }

    public byte[] ToArray()
    {
        byte[] result = new byte[checked((int)_written)];
        int offset = 0;
        for (int i = 0; i < _blocks.Count; i++)
        {
            byte[] block = _blocks[i];
            int count = (i == _blocks.Count - 1) ? _posInLast : _blockLengths[i];
            if (count > 0)
            {
                Buffer.BlockCopy(block, 0, result, offset, count);
                offset += count;
            }
        }
        return result;
    }

    public void Reset()
    {
        if (_blocks.Count > 1)
        {
            for (int i = 1; i < _blocks.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(_blocks[i]);
            }
            _blocks.RemoveRange(1, _blocks.Count - 1);
            _blockLengths.RemoveRange(1, _blockLengths.Count - 1);
        }
        if (_blockLengths.Count > 0)
        {
            _blockLengths[0] = 0;
        }
        _posInLast = 0;
        _written = 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (byte[] b in _blocks)
            {
                ArrayPool<byte>.Shared.Return(b);
            }
            _blocks.Clear();
            _blockLengths.Clear();
            _posInLast = 0;
            _written = 0;
            _disposed = true;
        }
    }
}
