namespace Glacier.Graphics.Tests;

using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Graphics;
using Glacier.Graphics.Codecs.Jpeg;
using Glacier.Graphics.Raster;
using Xunit;

public sealed class JpegCodecTests
{
    [Fact]
    public void AanIdct_DcOnlyBlock_ReconstructsUniformValues()
    {
        Span<float> block = stackalloc float[64];
        block.Clear();
        // DC coefficient = 800
        block[0] = 800f;

        JpegAanIdct.Transform8x8(block);

        // Constant field value should be 800 * 0.125 = 100
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(100f, block[i], precision: 4);
        }
    }

    [Fact]
    public void JpegColorTransform_StandardColors()
    {
        // White: Y=255, Cb=128, Cr=128
        Rgba32 white = JpegColorTransform.ConvertPixel(255f, 128f, 128f);
        Assert.Equal(255, white.R);
        Assert.Equal(255, white.G);
        Assert.Equal(255, white.B);

        // Black: Y=0, Cb=128, Cr=128
        Rgba32 black = JpegColorTransform.ConvertPixel(0f, 128f, 128f);
        Assert.Equal(0, black.R);
        Assert.Equal(0, black.G);
        Assert.Equal(0, black.B);
    }

    [Fact]
    public void JpegDecoder_DecodesSyntheticJpeg()
    {
        byte[] jpegBytes = CreateMinimalBaselineJpeg(8, 8);
        using var fb = JpegDecoder.Decode(jpegBytes);

        Assert.Equal(8, fb.Width);
        Assert.Equal(8, fb.Height);

        // All pixels should be near middle gray (128)
        Rgba32 pixel = fb.GetPixel(4, 4);
        Assert.True(pixel.R >= 120 && pixel.R <= 136);
        Assert.True(pixel.G >= 120 && pixel.G <= 136);
        Assert.True(pixel.B >= 120 && pixel.B <= 136);
    }

    private static byte[] CreateMinimalBaselineJpeg(int width, int height)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // SOI
        w.Write((byte)0xFF); w.Write((byte)0xD8);

        // DQT (Table 0: flat 1s)
        w.Write((byte)0xFF); w.Write((byte)0xDB);
        WriteUInt16BE(w, 67); // length
        w.Write((byte)0);     // precision 0, table 0
        for (int i = 0; i < 64; i++) w.Write((byte)1);

        // SOF0 (Baseline DCT, 1 component: Grayscale)
        w.Write((byte)0xFF); w.Write((byte)0xC0);
        WriteUInt16BE(w, 11);
        w.Write((byte)8); // 8-bit precision
        WriteUInt16BE(w, (ushort)height);
        WriteUInt16BE(w, (ushort)width);
        w.Write((byte)1); // 1 component (Y)
        w.Write((byte)1); // Comp ID 1
        w.Write((byte)0x11); // 1x1 sampling
        w.Write((byte)0); // QTable 0

        // DHT (DC table 0)
        w.Write((byte)0xFF); w.Write((byte)0xC4);
        WriteUInt16BE(w, 19 + 1); // length
        w.Write((byte)0x00);      // class 0 (DC), table 0
        // 16 counts: 1 code of length 1 (symbol 0: DC delta 0)
        w.Write((byte)1);
        for (int i = 1; i < 16; i++) w.Write((byte)0);
        w.Write((byte)0); // symbol 0 (0-bit category: diff=0)

        // DHT (AC table 0)
        w.Write((byte)0xFF); w.Write((byte)0xC4);
        WriteUInt16BE(w, 19 + 1);
        w.Write((byte)0x10); // class 1 (AC), table 0
        w.Write((byte)1);    // 1 code of length 1 (symbol 0: EOB)
        for (int i = 1; i < 16; i++) w.Write((byte)0);
        w.Write((byte)0); // EOB symbol

        // SOS
        w.Write((byte)0xFF); w.Write((byte)0xDA);
        WriteUInt16BE(w, 8);
        w.Write((byte)1); // 1 component
        w.Write((byte)1); // comp 1
        w.Write((byte)0x00); // DC 0, AC 0
        w.Write((byte)0); // Spectral start
        w.Write((byte)63); // Spectral end
        w.Write((byte)0); // Ah/Al

        // Scan payload: 1 MCU (8x8):
        // DC code: '0' (1 bit), AC code: '0' (1 bit for EOB).
        // Total 2 bits '00', padded with 1s to full byte: 0b00111111 = 0x3F
        w.Write((byte)0x3F);

        // EOI
        w.Write((byte)0xFF); w.Write((byte)0xD9);

        return ms.ToArray();
    }

    private static void WriteUInt16BE(BinaryWriter w, ushort val)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, val);
        w.Write(b);
    }
}
