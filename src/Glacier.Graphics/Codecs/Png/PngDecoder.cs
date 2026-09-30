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

        using var idatStream = new MemoryStream();

        while (offset + 8 <= pngData.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(pngData.Slice(offset, 4));
            string type = System.Text.Encoding.ASCII.GetString(pngData.Slice(offset + 4, 4));
            offset += 8;

            if (offset + length + 4 > pngData.Length)
                throw new FormatException("Premature end of PNG data.");

            ReadOnlySpan<byte> chunkData = pngData.Slice(offset, length);
            offset += length + 4; // Skip chunk data + 4 bytes CRC

            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(chunkData.Slice(0, 4));
                height = BinaryPrimitives.ReadInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];

                if (bitDepth != 8)
                    throw new NotSupportedException($"Only 8-bit PNGs are supported, got {bitDepth} bits.");
                if (colorType != 6 && colorType != 2)
                    throw new NotSupportedException($"Only RGBA (type 6) and RGB (type 2) PNGs are supported, got type {colorType}.");
            }
            else if (type == "IDAT")
            {
                idatStream.Write(chunkData);
            }
            else if (type == "IEND")
            {
                break;
            }
        }

        if (width <= 0 || height <= 0)
            throw new FormatException("Invalid PNG image dimensions.");

        // Decompress IDAT payload
        idatStream.Position = 0;
        using var zlib = new ZLibStream(idatStream, CompressionMode.Decompress);
        using var decompressedStream = new MemoryStream();
        zlib.CopyTo(decompressedStream);
        byte[] rawPixels = decompressedStream.ToArray();

        int bytesPerPixel = colorType == 6 ? 4 : 3;
        int rowDataBytes = width * bytesPerPixel;
        int strideWithFilter = rowDataBytes + 1;

        if (rawPixels.Length < strideWithFilter * height)
            throw new FormatException("Decompressed pixel data size is smaller than expected.");

        var framebuffer = new LinearFramebuffer(width, height);
        byte[] curRow = new byte[rowDataBytes];
        byte[] priorRow = new byte[rowDataBytes];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * strideWithFilter;
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
