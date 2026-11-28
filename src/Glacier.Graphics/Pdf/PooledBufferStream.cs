namespace Glacier.Graphics.Pdf;

using System;
using System.IO;

/// <summary>
/// Write-only <see cref="Stream"/> adapter over a <see cref="PooledBufferWriter"/>.
/// </summary>
internal sealed class PooledBufferStream : Stream
{
    private readonly PooledBufferWriter _writer;

    public PooledBufferStream(PooledBufferWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        _writer.Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _writer.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        _writer.WriteByte(value);
    }
}
