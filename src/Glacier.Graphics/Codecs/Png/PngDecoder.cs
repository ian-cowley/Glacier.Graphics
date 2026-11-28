namespace Glacier.Graphics.Codecs.Png;

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Glacier.Graphics.Raster;

/// <summary>
/// Hardware-accelerated PNG decoder with vectorized filter reconstruction.
/// Reads RFC 2083 PNG datastreams directly into LinearFramebuffer.
/// </summary>
public static class PngDecoder
{
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private const long MaxImagePixels = 128_000_000L;
    private const long MaxDecompressedBytes = 512L * 1024L * 1024L;

    public static LinearFramebuffer Decode(byte[] pngData)
    {
        if (pngData == null) throw new ArgumentNullException(nameof(pngData));
        return Decode(pngData.AsSpan());
    }

    public static LinearFramebuffer Decode(ReadOnlySpan<byte> pngData)
    {
        if (pngData.Length < 8 || !pngData.Slice(0, 8).SequenceEqual(PngHeader))
            throw new FormatException("Invalid PNG signature.");

        int offset = 8;
        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        bool sawIhdr = false;
        bool sawIend = false;

        using var idatStream = new MemoryStream();

        while (offset + 8 <= pngData.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(pngData.Slice(offset, 4));
            if (length < 0 || (long)offset + 8 + length + 4 > pngData.Length)
                throw new FormatException("Invalid PNG chunk length or premature end of PNG data.");

            ReadOnlySpan<byte> typeAndData = pngData.Slice(offset + 4, 4 + length);
            string type = System.Text.Encoding.ASCII.GetString(pngData.Slice(offset + 4, 4));
            offset += 8;

            ReadOnlySpan<byte> chunkData = pngData.Slice(offset, length);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(pngData.Slice(offset + length, 4));
            uint actualCrc = PngEncoder.ComputeCrc(typeAndData);
            if (actualCrc != expectedCrc)
                throw new InvalidDataException($"PNG chunk '{type}' CRC-32 mismatch (expected 0x{expectedCrc:X8}, computed 0x{actualCrc:X8}).");

            offset += length + 4;

            if (type == "IHDR")
            {
                if (chunkData.Length < 13)
                    throw new FormatException("Invalid PNG IHDR chunk length.");

                width = BinaryPrimitives.ReadInt32BigEndian(chunkData.Slice(0, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                sawIhdr = true;

                if (width <= 0 || height <= 0)
                    throw new FormatException("Invalid PNG image dimensions.");
                if ((long)width * height > MaxImagePixels)
                    throw new InvalidDataException($"PNG dimensions {width}x{height} exceed maximum supported pixel count ({MaxImagePixels}).");
                if (bitDepth != 8)
                    throw new NotSupportedException($"Only 8-bit PNGs are supported, got {bitDepth} bits.");
                if (colorType != 6 && colorType != 2)
                    throw new NotSupportedException($"Only RGBA (type 6) and RGB (type 2) PNGs are supported, got type {colorType}.");
            }
            else if (type == "IDAT")
            {
                if (!sawIhdr)
                    throw new InvalidDataException("PNG IDAT chunk encountered before IHDR.");
                idatStream.Write(chunkData);
            }
            else if (type == "IEND")
            {
                sawIend = true;
                break;
            }
        }

        if (!sawIhdr || width <= 0 || height <= 0 || !sawIend)
            throw new FormatException("Invalid or incomplete PNG datastream.");

        int bytesPerPixel = colorType == 6 ? 4 : 3;
        long rowDataBytesLong = (long)width * bytesPerPixel;
        long strideWithFilterLong = rowDataBytesLong + 1;
        long expectedRawBytes = strideWithFilterLong * height;
        if (expectedRawBytes > MaxDecompressedBytes || rowDataBytesLong > int.MaxValue || strideWithFilterLong > int.MaxValue)
            throw new InvalidDataException("PNG image buffer size exceeds maximum decompression limit.");

        int rowDataBytes = (int)rowDataBytesLong;
        int strideWithFilter = (int)strideWithFilterLong;

        // Decompress IDAT payload with bounded read cap
        idatStream.Position = 0;
        using var zlib = new ZLibStream(idatStream, CompressionMode.Decompress);
        using var decompressedStream = new MemoryStream((int)Math.Min(expectedRawBytes, 64 * 1024 * 1024));
        byte[] copyBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            long totalRead = 0;
            int read;
            while ((read = zlib.Read(copyBuf, 0, copyBuf.Length)) > 0)
            {
                totalRead += read;
                if (totalRead > MaxDecompressedBytes)
                    throw new InvalidDataException("PNG decompressed stream exceeds maximum allowed size.");
                decompressedStream.Write(copyBuf, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(copyBuf);
        }

        byte[] rawPixels = decompressedStream.GetBuffer();
        int rawLength = (int)decompressedStream.Length;

        if (rawLength < expectedRawBytes)
            throw new FormatException("Decompressed pixel data size is smaller than expected.");

        var framebuffer = new LinearFramebuffer(width, height);
        byte[] curRow = new byte[rowDataBytes];
        byte[] priorRow = new byte[rowDataBytes];

        for (int y = 0; y < height; y++)
        {
            int rowStart = checked(y * strideWithFilter);
            byte filterType = rawPixels[rowStart];
            rawPixels.AsSpan(rowStart + 1, rowDataBytes).CopyTo(curRow);

            PngFilters.ReconstructScanline(filterType, curRow, y > 0 ? priorRow : ReadOnlySpan<byte>.Empty, bytesPerPixel);

            var dstRow = framebuffer.GetRowSpan(y);

            if (colorType == 6) // RGBA
            {
                var srcRgba = MemoryMarshal.Cast<byte, Rgba32>(curRow.AsSpan());
                srcRgba.CopyTo(dstRow);
            }
            else // RGB
            {
                for (int x = 0; x < width; x++)
                {
                    int s = x * 3;
                    dstRow[x] = new Rgba32(curRow[s], curRow[s + 1], curRow[s + 2], 255);
                }
            }

            Array.Copy(curRow, priorRow, rowDataBytes);
        }

        return framebuffer;
    }
}
