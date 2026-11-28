namespace Glacier.Graphics.Pdf;

using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;

/// <summary>
/// Shared zero-churn PDF 1.7 serialization primitives backed by <see cref="IBufferWriter{T}"/>.
/// </summary>
internal static class Pdf17Writer
{
    private static readonly byte[] Header17Bytes =
    [
        (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1', (byte)'.', (byte)'7', (byte)'\n',
        (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n'
    ];

    public static void WriteHeader(IBufferWriter<byte> w)
    {
        Span<byte> dst = w.GetSpan(Header17Bytes.Length);
        Header17Bytes.CopyTo(dst);
        w.Advance(Header17Bytes.Length);
    }

    public static void WriteAscii(IBufferWriter<byte> w, ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return;
        Span<byte> span = w.GetSpan(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            span[i] = (byte)s[i];
        }
        w.Advance(s.Length);
    }

    public static void WriteAscii(IBufferWriter<byte> w, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        Span<byte> span = w.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        w.Advance(bytes.Length);
    }

    public static void WriteLatin1(IBufferWriter<byte> w, ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return;
        Span<byte> span = w.GetSpan(s.Length);
        int written = Encoding.Latin1.GetBytes(s, span);
        w.Advance(written);
    }

    public static void WriteUtf8(IBufferWriter<byte> w, ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return;
        int byteCount = Encoding.UTF8.GetByteCount(chars);
        Span<byte> span = w.GetSpan(byteCount);
        int written = Encoding.UTF8.GetBytes(chars, span);
        w.Advance(written);
    }

    public static void WriteInt32(IBufferWriter<byte> w, int value)
    {
        Span<byte> span = w.GetSpan(16);
        if (Utf8Formatter.TryFormat(value, span, out int written))
        {
            w.Advance(written);
        }
    }

    public static void WriteInt64(IBufferWriter<byte> w, long value)
    {
        Span<byte> span = w.GetSpan(24);
        if (Utf8Formatter.TryFormat(value, span, out int written))
        {
            w.Advance(written);
        }
    }

    public static void WriteFixed10(IBufferWriter<byte> w, long value)
    {
        Span<byte> span = w.GetSpan(16);
        if (Utf8Formatter.TryFormat(value, span, out int written, new StandardFormat('D', 10)))
        {
            w.Advance(written);
        }
    }

    public static void WriteDouble(IBufferWriter<byte> w, double val)
    {
        Span<byte> span = w.GetSpan(32);
        if (Utf8Formatter.TryFormat(val, span, out int written))
        {
            w.Advance(written);
            return;
        }
        WriteAscii(w, val.ToString(CultureInfo.InvariantCulture));
    }

    public static void WriteDoubleFixed(IBufferWriter<byte> w, double val, byte precision)
    {
        Span<byte> span = w.GetSpan(32);
        if (Utf8Formatter.TryFormat(val, span, out int written, new StandardFormat('F', precision)))
        {
            w.Advance(written);
            return;
        }
        WriteAscii(w, val.ToString(CultureInfo.InvariantCulture));
    }

    public static void WriteFloatCompact(IBufferWriter<byte> w, float val, int maxDecimals = 2)
    {
        Span<char> charBuf = stackalloc char[32];
        string fmt = maxDecimals == 3 ? "0.###" : "0.##";
        if (val.TryFormat(charBuf, out int charsWritten, fmt, CultureInfo.InvariantCulture))
        {
            WriteAscii(w, charBuf.Slice(0, charsWritten));
            return;
        }
        WriteAscii(w, val.ToString(fmt, CultureInfo.InvariantCulture));
    }

    public static void WriteXrefAndTrailer(
        IBufferWriter<byte> w,
        ReadOnlySpan<long> offsets,
        int objectCount,
        int rootObjectId,
        long xrefOffset)
    {
        WriteAscii(w, "xref\n0 "u8);
        WriteInt32(w, objectCount);
        WriteAscii(w, "\n0000000000 65535 f \n"u8);

        for (int i = 1; i < objectCount; i++)
        {
            WriteFixed10(w, offsets[i]);
            WriteAscii(w, " 00000 n \n"u8);
        }

        WriteAscii(w, "trailer\n<< /Size "u8);
        WriteInt32(w, objectCount);
        WriteAscii(w, " /Root "u8);
        WriteInt32(w, rootObjectId);
        WriteAscii(w, " 0 R >>\nstartxref\n"u8);
        WriteInt64(w, xrefOffset);
        WriteAscii(w, "\n%%EOF\n"u8);
    }
}
