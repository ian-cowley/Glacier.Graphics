namespace Glacier.Graphics.Codecs.Jpeg;

using System;
using System.Buffers.Binary;
using Glacier.Graphics.Raster;

/// <summary>
/// High-performance baseline JPEG decoder with AAN SIMD IDCT, 4:4:4 / 4:2:2 / 4:2:0 chroma upsampling,
/// restart interval support, and YCbCr color transform.
/// Fully managed pure C# implementation with zero external dependencies.
/// </summary>
public static class JpegDecoder
{
    private static readonly byte[] ZigZag =
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    ];

    private sealed class ComponentInfo
    {
        public int Id;
        public int HFactor;
        public int VFactor;
        public int QuantTableId;
        public int DcHuffmanId;
        public int AcHuffmanId;
        public int PreviousDc;
        public float[] McuSamples = [];
    }

    public static LinearFramebuffer Decode(byte[] jpegData)
    {
        ArgumentNullException.ThrowIfNull(jpegData);
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
        int restartInterval = 0;
        ComponentInfo[]? components = null;

        float[][] quantTables = new float[4][];
        JpegHuffmanTable[] dcHuffman = new JpegHuffmanTable[4];
        JpegHuffmanTable[] acHuffman = new JpegHuffmanTable[4];

        for (int i = 0; i < 4; i++)
        {
            quantTables[i] = new float[64];
            Array.Fill(quantTables[i], 1f);
            dcHuffman[i] = new JpegHuffmanTable();
            acHuffman[i] = new JpegHuffmanTable();
        }

        while (offset + 2 <= span.Length)
        {
            if (span[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            while (offset + 1 < span.Length && span[offset + 1] == 0xFF)
            {
                offset++;
            }
            if (offset + 1 >= span.Length)
            {
                break;
            }

            byte marker = span[offset + 1];
            offset += 2;

            if (marker == 0xD9) // EOI
                break;

            if (marker == 0x00 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;

            if (offset + 2 > span.Length)
                throw new FormatException("Truncated JPEG segment header.");

            int length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
            if (length < 2 || offset + length > span.Length)
                throw new FormatException("Invalid JPEG segment length.");

            int payloadOffset = offset + 2;
            int payloadLength = length - 2;

            if (marker == 0xDB) // DQT (Define Quantization Table)
            {
                int qOffset = payloadOffset;
                int endQ = payloadOffset + payloadLength;
                while (qOffset < endQ)
                {
                    byte info = span[qOffset++];
                    int precision = (info >> 4) & 0x0F;
                    int tableId = info & 0x03;
                    float[] table = new float[64];
                    for (int k = 0; k < 64; k++)
                    {
                        if (precision == 0)
                        {
                            table[ZigZag[k]] = span[qOffset++];
                        }
                        else
                        {
                            table[ZigZag[k]] = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(qOffset, 2));
                            qOffset += 2;
                        }
                    }
                    quantTables[tableId] = table;
                }
            }
            else if (marker == 0xDD && payloadLength >= 2) // DRI (Define Restart Interval)
            {
                restartInterval = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(payloadOffset, 2));
            }
            else if (marker is 0xC0 or 0xC1) // SOF0 / SOF1 (Baseline / Extended Sequential DCT)
            {
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

                    int hFactor = Math.Max(1, (factors >> 4) & 0x0F);
                    int vFactor = Math.Max(1, factors & 0x0F);

                    components[c] = new ComponentInfo
                    {
                        Id = id,
                        HFactor = hFactor,
                        VFactor = vFactor,
                        QuantTableId = qId & 0x03,
                        McuSamples = new float[hFactor * 8 * vFactor * 8]
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
                    int tableId = info & 0x03;

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
                                comp.DcHuffmanId = (huffmanIds >> 4) & 0x03;
                                comp.AcHuffmanId = huffmanIds & 0x03;
                            }
                        }
                    }
                }

                offset += length; // Point to start of entropy stream
                break;
            }

            offset += length;
        }

        if (width <= 0 || height <= 0 || components == null || components.Length == 0)
            throw new FormatException("Invalid or incomplete JPEG header.");

        var framebuffer = new LinearFramebuffer(width, height);
        var bitReader = new JpegBitReader(jpegData, offset);

        DecodeScan(framebuffer, bitReader, width, height, components, quantTables, dcHuffman, acHuffman, restartInterval);

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
        JpegHuffmanTable[] acHuffman,
        int restartInterval)
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

        bool isFast444Color = components.Length >= 3 && maxH == 1 && maxV == 1;
        Span<float> blockScratch = stackalloc float[64];
        Span<Rgba32> blockRgba = stackalloc Rgba32[64];

        int mcuCounter = 0;

        for (int my = 0; my < mcusY; my++)
        {
            for (int mx = 0; mx < mcusX; mx++)
            {
                if (restartInterval > 0 && mcuCounter > 0 && (mcuCounter % restartInterval) == 0)
                {
                    reader.ResetRestartMarker();
                    for (int c = 0; c < components.Length; c++)
                    {
                        components[c].PreviousDc = 0;
                    }
                }
                mcuCounter++;

                // Decode all (VFactor x HFactor) 8x8 blocks for each component in the interleaved MCU
                for (int c = 0; c < components.Length; c++)
                {
                    var comp = components[c];
                    int compStride = comp.HFactor * 8;
                    var qTable = quantTables[comp.QuantTableId];
                    var dcTab = dcHuffman[comp.DcHuffmanId];
                    var acTab = acHuffman[comp.AcHuffmanId];

                    for (int vy = 0; vy < comp.VFactor; vy++)
                    {
                        for (int hx = 0; hx < comp.HFactor; hx++)
                        {
                            DecodeBlock(reader, blockScratch, comp, qTable, dcTab, acTab);
                            JpegAanIdct.Transform8x8(blockScratch);

                            int dstBaseY = vy * 8;
                            int dstBaseX = hx * 8;
                            for (int by = 0; by < 8; by++)
                            {
                                int dstRowOffset = (dstBaseY + by) * compStride + dstBaseX;
                                int srcRowOffset = by * 8;
                                for (int bx = 0; bx < 8; bx++)
                                {
                                    comp.McuSamples[dstRowOffset + bx] = blockScratch[srcRowOffset + bx] + 128f;
                                }
                            }
                        }
                    }
                }

                int startPixelX = mx * mcuWidth;
                int startPixelY = my * mcuHeight;

                if (isFast444Color)
                {
                    JpegColorTransform.ConvertBlock(
                        components[0].McuSamples,
                        components[1].McuSamples,
                        components[2].McuSamples,
                        blockRgba);

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
                else if (components.Length >= 3)
                {
                    // General subsampled YCbCr (e.g., 4:2:0 H=2,V=2 or 4:2:2 H=2,V=1) with bilinear chroma upsampling
                    var yComp = components[0];
                    var cbComp = components[1];
                    var crComp = components[2];

                    int yW = yComp.HFactor * 8;
                    int yH = yComp.VFactor * 8;
                    int cbW = cbComp.HFactor * 8;
                    int cbH = cbComp.VFactor * 8;
                    int crW = crComp.HFactor * 8;
                    int crH = crComp.VFactor * 8;

                    for (int pyMcu = 0; pyMcu < mcuHeight; pyMcu++)
                    {
                        int py = startPixelY + pyMcu;
                        if (py >= height) break;
                        var row = fb.GetRowSpan(py);

                        for (int pxMcu = 0; pxMcu < mcuWidth; pxMcu++)
                        {
                            int px = startPixelX + pxMcu;
                            if (px >= width) break;

                            float yVal = SampleComponentBilinear(yComp.McuSamples, yW, yH, pxMcu, pyMcu, mcuWidth, mcuHeight);
                            float cbVal = SampleComponentBilinear(cbComp.McuSamples, cbW, cbH, pxMcu, pyMcu, mcuWidth, mcuHeight);
                            float crVal = SampleComponentBilinear(crComp.McuSamples, crW, crH, pxMcu, pyMcu, mcuWidth, mcuHeight);

                            row[px] = JpegColorTransform.ConvertPixel(yVal, cbVal, crVal);
                        }
                    }
                }
                else
                {
                    // Grayscale (1 component)
                    var yComp = components[0];
                    int yW = yComp.HFactor * 8;
                    for (int pyMcu = 0; pyMcu < mcuHeight; pyMcu++)
                    {
                        int py = startPixelY + pyMcu;
                        if (py >= height) break;
                        var row = fb.GetRowSpan(py);

                        for (int pxMcu = 0; pxMcu < mcuWidth; pxMcu++)
                        {
                            int px = startPixelX + pxMcu;
                            if (px >= width) break;

                            int sy = (pyMcu * yComp.VFactor) / maxV;
                            int sx = (pxMcu * yComp.HFactor) / maxH;
                            byte lum = (byte)Math.Clamp((int)MathF.Round(yComp.McuSamples[sy * yW + sx]), 0, 255);
                            row[px] = new Rgba32(lum, lum, lum, 255);
                        }
                    }
                }
            }
        }
    }

    private static float SampleComponentBilinear(
        float[] samples,
        int compW,
        int compH,
        int pxMcu,
        int pyMcu,
        int mcuW,
        int mcuH)
    {
        if (compW == mcuW && compH == mcuH)
        {
            return samples[pyMcu * compW + pxMcu];
        }

        // Map MCU pixel center (pxMcu + 0.5, pyMcu + 0.5) into subsampled component coordinates
        float u = ((pxMcu + 0.5f) * compW / mcuW) - 0.5f;
        float v = ((pyMcu + 0.5f) * compH / mcuH) - 0.5f;

        u = Math.Clamp(u, 0f, compW - 1);
        v = Math.Clamp(v, 0f, compH - 1);

        int x0 = (int)u;
        int y0 = (int)v;
        int x1 = Math.Min(x0 + 1, compW - 1);
        int y1 = Math.Min(y0 + 1, compH - 1);

        float fx = u - x0;
        float fy = v - y0;

        float s00 = samples[y0 * compW + x0];
        float s10 = samples[y0 * compW + x1];
        float s01 = samples[y1 * compW + x0];
        float s11 = samples[y1 * compW + x1];

        float top = s00 + (s10 - s00) * fx;
        float bottom = s01 + (s11 - s01) * fx;
        return top + (bottom - top) * fy;
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
