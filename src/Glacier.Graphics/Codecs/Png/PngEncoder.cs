namespace Glacier.Graphics.Codecs.Png;

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Glacier.Graphics.Raster;

/// <summary>
/// Hardware-accelerated PNG encoder for RGBA32 and BGRA32 framebuffers.
/// Produces compliant RFC 2083 PNG datastreams with ZLIB compression and CRC-32 integrity.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = CreateCrcTable();

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                if ((c & 1) != 0)
                    c = 0xEDB88320u ^ (c >> 1);
                else
                    c >>= 1;
            }
            table[i] = c;
        }
        return table;
    }

    public static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
        {
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    public static byte[] Encode(LinearFramebuffer framebuffer)
    {
        if (framebuffer == null) throw new ArgumentNullException(nameof(framebuffer));
        using var ms = new MemoryStream();
        Encode(framebuffer, ms);
        return ms.ToArray();
    }

    public static void Encode(LinearFramebuffer framebuffer, Stream output)
    {
        if (framebuffer == null) throw new ArgumentNullException(nameof(framebuffer));
        if (output == null) throw new ArgumentNullException(nameof(output));

        int width = framebuffer.Width;
        int height = framebuffer.Height;

        // 1. Signature
        output.Write(PngHeader, 0, PngHeader.Length);

        // 2. IHDR
        byte[] ihdrData = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdrData.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdrData.AsSpan(4, 4), height);
        ihdrData[8] = 8; // 8 bits per channel
        ihdrData[9] = 6; // Color type 6 = RGBA
        ihdrData[10] = 0; // Compression Deflate
        ihdrData[11] = 0; // Filter method adaptive
        ihdrData[12] = 0; // Interlace none
        WriteChunk(output, "IHDR", ihdrData);

        // 3. Filter rows & Compress into IDAT
        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            int rowBytes = width * 4;
            byte[] filterBuffer = new byte[rowBytes + 1];
            ReadOnlySpan<byte> priorRow = default;

            for (int y = 0; y < height; y++)
            {
                var curRow = framebuffer.GetRowSpan(y);
                ReadOnlySpan<byte> curBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(curRow);

                // Filter 1: Sub filter
                filterBuffer[0] = 1;
                PngFilters.ApplyFilter(1, curBytes, priorRow, filterBuffer.AsSpan(1, rowBytes), 4);

                zlib.Write(filterBuffer, 0, filterBuffer.Length);
                priorRow = curBytes;
            }
        }

        byte[] compressedData = compressedStream.ToArray();
        WriteChunk(output, "IDAT", compressedData);

        // 4. IEND
        WriteChunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        output.Write(lenBytes);

        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes, 0, 4);

        if (!data.IsEmpty)
        {
            output.Write(data);
        }

        // CRC over type + data
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < 4; i++)
            crc = CrcTable[(crc ^ typeBytes[i]) & 0xFF] ^ (crc >> 8);

        for (int i = 0; i < data.Length; i++)
            crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

        crc ^= 0xFFFFFFFFu;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }
}
