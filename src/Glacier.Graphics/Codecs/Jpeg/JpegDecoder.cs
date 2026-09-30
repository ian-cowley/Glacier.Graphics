namespace Glacier.Graphics.Codecs.Jpeg;

using System;
using System.Buffers.Binary;
using Glacier.Graphics.Raster;

/// <summary>
/// High-performance baseline JPEG decoder with AAN SIMD IDCT and YCbCr color transform.
/// Fully managed pure C# implementation with zero external dependencies.
/// </summary>
public static class JpegDecoder
{
    private static readonly byte[] ZigZag =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    };

    private sealed class ComponentInfo
    {
        public int Id;
        public int HFactor;
        public int VFactor;
        public int QuantTableId;
        public int DcHuffmanId;
        public int AcHuffmanId;
        public int PreviousDc;
    }

    public static LinearFramebuffer Decode(byte[] jpegData)
    {
        if (jpegData == null) throw new ArgumentNullException(nameof(jpegData));
        return Decode(jpegData.AsMemory());
    }

    public static LinearFramebuffer Decode(ReadOnlyMemory<byte> jpegData)
    {
        var span = jpegData.Span;
        if (span.Length < 4 || span[0] != 0xFF || span[1] != 0xD8)
            throw new FormatException("Invalid JPEG signature (missing SOI).");

        int offset = 2;
        int width = 0;
        int height = 0;
        ComponentInfo[]? components = null;

        float[][] quantTables = new float[4][];
        JpegHuffmanTable[] dcHuffman = new JpegHuffmanTable[4];
        JpegHuffmanTable[] acHuffman = new JpegHuffmanTable[4];

        for (int i = 0; i < 4; i++)
        {
            dcHuffman[i] = new JpegHuffmanTable();
            acHuffman[i] = new JpegHuffmanTable();
        }

        while (offset + 4 <= span.Length)
        {
            if (span[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            byte marker = span[offset + 1];
            offset += 2;

            if (marker == 0xD9) // EOI
                break;

            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) // Padding or RST
                continue;

            int length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
            int payloadOffset = offset + 2;
            int payloadLength = length - 2;

            if (marker == 0xDB) // DQT (Define Quantization Table)
            {
                int qOffset = payloadOffset;
                int endQ = payloadOffset + payloadLength;
                while (qOffset < endQ)
                {
                    byte info = span[qOffset++];
                    int tableId = info & 0x0F;
                    float[] table = new float[64];
                    for (int k = 0; k < 64; k++)
                    {
                        table[k] = span[qOffset++];
                    }
                    quantTables[tableId] = table;
                }
            }
            else if (marker == 0xC0) // SOF0 (Start of Frame - Baseline DCT)
            {
                byte precision = span[payloadOffset];
                height = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(payloadOffset + 1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(payloadOffset + 3, 2));
                int numComponents = span[payloadOffset + 5];

                components = new ComponentInfo[numComponents];
                for (int c = 0; c < numComponents; c++)
                {
                    int compOffset = payloadOffset + 6 + (c * 3);
                    byte id = span[compOffset];
                    byte factors = span[compOffset + 1];
                    byte qId = span[compOffset + 2];

                    components[c] = new ComponentInfo
                    {
                        Id = id,
                        HFactor = (factors >> 4) & 0x0F,
                        VFactor = factors & 0x0F,
                        QuantTableId = qId
                    };
                }
            }
            else if (marker == 0xC4) // DHT (Define Huffman Table)
            {
                int hOffset = payloadOffset;
                int endH = payloadOffset + payloadLength;
                while (hOffset < endH)
                {
                    byte info = span[hOffset++];
                    int tableClass = (info >> 4) & 0x0F; // 0 = DC, 1 = AC
                    int tableId = info & 0x0F;

                    ReadOnlySpan<byte> bits = span.Slice(hOffset, 16);
                    hOffset += 16;

                    int numSymbols = 0;
                    for (int k = 0; k < 16; k++) numSymbols += bits[k];

                    ReadOnlySpan<byte> symbols = span.Slice(hOffset, numSymbols);
                    hOffset += numSymbols;

                    if (tableClass == 0)
                        dcHuffman[tableId].Initialize(bits, symbols);
                    else
                        acHuffman[tableId].Initialize(bits, symbols);
                }
            }
            else if (marker == 0xDA) // SOS (Start of Scan)
            {
                int numCompInScan = span[payloadOffset];
                for (int c = 0; c < numCompInScan; c++)
                {
                    int scanCompOffset = payloadOffset + 1 + (c * 2);
                    byte compId = span[scanCompOffset];
                    byte huffmanIds = span[scanCompOffset + 1];

                    if (components != null)
                    {
                        foreach (var comp in components)
                        {
                            if (comp.Id == compId)
                            {
                                comp.DcHuffmanId = (huffmanIds >> 4) & 0x0F;
                                comp.AcHuffmanId = huffmanIds & 0x0F;
                            }
                        }
                    }
                }

                offset += length; // Point to start of entropy stream
                break;
            }

            offset += length;
        }

        if (width <= 0 || height <= 0 || components == null)
            throw new FormatException("Invalid or incomplete JPEG header.");

        var framebuffer = new LinearFramebuffer(width, height);
        var bitReader = new JpegBitReader(jpegData, offset);

        DecodeScan(framebuffer, bitReader, width, height, components, quantTables, dcHuffman, acHuffman);

        return framebuffer;
    }

    private static void DecodeScan(
        LinearFramebuffer fb,
        JpegBitReader reader,
        int width,
        int height,
        ComponentInfo[] components,
        float[][] quantTables,
        JpegHuffmanTable[] dcHuffman,
        JpegHuffmanTable[] acHuffman)
    {
        int maxH = 1, maxV = 1;
        foreach (var comp in components)
        {
            maxH = Math.Max(maxH, comp.HFactor);
            maxV = Math.Max(maxV, comp.VFactor);
        }

        int mcuWidth = maxH * 8;
        int mcuHeight = maxV * 8;
        int mcusX = (width + mcuWidth - 1) / mcuWidth;
        int mcusY = (height + mcuHeight - 1) / mcuHeight;

        Span<float> blockY = stackalloc float[64];
        Span<float> blockCb = stackalloc float[64];
        Span<float> blockCr = stackalloc float[64];
        Span<Rgba32> blockRgba = stackalloc Rgba32[64];

        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                // Decode Y component block
                var yComp = components[0];
                DecodeBlock(reader, blockY, yComp, quantTables[yComp.QuantTableId], dcHuffman[yComp.DcHuffmanId], acHuffman[yComp.AcHuffmanId]);

                if (components.Length >= 3)
                {
                    var cbComp = components[1];
                    DecodeBlock(reader, blockCb, cbComp, quantTables[cbComp.QuantTableId], dcHuffman[cbComp.DcHuffmanId], acHuffman[cbComp.AcHuffmanId]);

                    var crComp = components[2];
                    DecodeBlock(reader, blockCr, crComp, quantTables[crComp.QuantTableId], dcHuffman[crComp.DcHuffmanId], acHuffman[crComp.AcHuffmanId]);

                    // Apply IDCT
                    JpegAanIdct.Transform8x8(blockY);
                    JpegAanIdct.Transform8x8(blockCb);
                    JpegAanIdct.Transform8x8(blockCr);

                    // Add 128 level shift to Y, Cb, Cr
                    for (int i = 0; i < 64; i++)
                    {
                        blockY[i] += 128f;
                        blockCb[i] += 128f;
                        blockCr[i] += 128f;
                    }

                    // Vectorized YCbCr -> RGB
                    JpegColorTransform.ConvertBlock(blockY, blockCb, blockCr, blockRgba);
                }
                else
                {
                    // Grayscale
                    JpegAanIdct.Transform8x8(blockY);
                    for (int i = 0; i < 64; i++)
                    {
                        byte lum = (byte)Math.Clamp((int)MathF.Round(blockY[i] + 128f), 0, 255);
                        blockRgba[i] = new Rgba32(lum, lum, lum, 255);
                    }
                }

                // Write 8x8 block to framebuffer
                int startPixelX = mx * mcuWidth;
                int startPixelY = my * mcuHeight;

                for (int by = 0; by < 8; by++)
                {
                    int py = startPixelY + by;
                    if (py >= height) break;
                    var row = fb.GetRowSpan(py);

                    for (int bx = 0; bx < 8; bx++)
                    {
                        int px = startPixelX + bx;
                        if (px >= width) break;
                        row[px] = blockRgba[by * 8 + bx];
                    }
                }
            }
        }
    }

    private static void DecodeBlock(
        JpegBitReader reader,
        Span<float> block,
        ComponentInfo comp,
        float[] quantTable,
        JpegHuffmanTable dcTable,
        JpegHuffmanTable acTable)
    {
        block.Clear();

        // 1. Decode DC coefficient
        int dcCategory = dcTable.Decode(reader);
        int dcDelta = reader.ReadSignedBits(dcCategory);
        int dc = comp.PreviousDc + dcDelta;
        comp.PreviousDc = dc;
        block[0] = dc * quantTable[0];

        // 2. Decode AC coefficients
        int k = 1;
        while (k < 64)
        {
            int symbol = acTable.Decode(reader);
            int zeroRun = (symbol >> 4) & 0x0F;
            int category = symbol & 0x0F;

            if (category == 0)
            {
                if (zeroRun == 15) // ZRL: 16 zero run
                {
                    k += 16;
                }
                else // EOB: End of block
                {
                    break;
                }
            }
            else
            {
                k += zeroRun;
                if (k >= 64) break;
                int acVal = reader.ReadSignedBits(category);
                int naturalIndex = ZigZag[k];
                block[naturalIndex] = acVal * quantTable[naturalIndex];
                k++;
            }
        }
    }
}
