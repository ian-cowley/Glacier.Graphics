namespace Glacier.Graphics.Tests;

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using Glacier.Graphics;
using Glacier.Graphics.Codecs.Jpeg;
using Glacier.Graphics.Codecs.Png;
using Glacier.Graphics.Pdf;
using Glacier.Graphics.Raster;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class HardeningAndSimdTests
{
    [Fact]
    public void PorterDuffSrcOver_StraightAlpha_SemiTransparentDstAndSrc_Accurate()
    {
        // Straight-alpha SRC_OVER:
        // dst = (0, 0, 255, 128) (da = 128/255)
        // src = (255, 0, 0, 128) (sa = 128/255)
        // outA = sa + da*(1-sa) ~= 0.7529 -> 192
        // outR = (255*sa) / outA ~= 170, outB = (255*da*(1-sa)) / outA ~= 85
        using var fb = new LinearFramebuffer(16, 16);
        fb.Clear(new Rgba32(0, 0, 255, 128));

        var rasterizer = new CpuRasterizer();
        var rect = new VectorPath();
        rect.AddRect(2, 2, 12, 12);
        rasterizer.FillPath(fb, rect, new Rgba32(255, 0, 0, 128));

        Rgba32 blended = fb.GetPixel(8, 8);
        Assert.InRange(blended.A, 189, 195);
        Assert.InRange(blended.R, 166, 174);
        Assert.Equal(0, blended.G);
        Assert.InRange(blended.B, 81, 89);

        // TileComputeRasterizer must match straight-alpha SRC_OVER as well
        using var tileFb = new LinearFramebuffer(16, 16);
        tileFb.Clear(new Rgba32(0, 0, 255, 128));
        var tileRaster = new TileComputeRasterizer();
        tileRaster.Rasterize(tileFb, rect, new Rgba32(255, 0, 0, 128));

        Rgba32 tileBlended = tileFb.GetPixel(8, 8);
        Assert.InRange(tileBlended.A, 189, 195);
        Assert.InRange(tileBlended.R, 166, 174);
        Assert.Equal(0, tileBlended.G);
        Assert.InRange(tileBlended.B, 81, 89);
    }

    [Fact]
    public void DrawTextAndDrawImage_SemiTransparentAlpha_DoesNotSquareAlpha()
    {
        // DrawText over Black with A=128 should produce maxR >= 96 (not <= 64 from alpha squaring)
        using var textFb = new LinearFramebuffer(64, 32);
        using (var canvas = new CpuGraphicsCanvas(textFb))
        {
            canvas.Clear(Rgba32.Black);
            canvas.DrawText("X", 8f, 20f, new Font(16f), new Paint(new Rgba32(255, 0, 0, 128)));
        }

        byte maxR = 0;
        foreach (var px in textFb.AsRgbaSpan())
        {
            if (px.R > maxR) maxR = px.R;
        }
        Assert.True(maxR >= 96, $"Expected linear single alpha (>= 96 red), got {maxR}.");

        // DrawImage over Black with A=128 should produce R ~= 128 (not ~64 from alpha squaring)
        using var srcImg = new LinearFramebuffer(4, 4);
        srcImg.Clear(new Rgba32(255, 0, 0, 128));

        using var dstFb = new LinearFramebuffer(8, 8);
        using (var canvas = new CpuGraphicsCanvas(dstFb))
        {
            canvas.Clear(Rgba32.Black);
            canvas.DrawImage(srcImg.AsReadOnlySpan2D(), 0, 0, 8, 8);
        }

        Rgba32 imgPx = dstFb.GetPixel(4, 4);
        Assert.InRange(imgPx.R, 125, 131);
        Assert.Equal(255, imgPx.A);
    }

    [Fact]
    public unsafe void LinearFramebuffer_ExternalMemoryValidationAndPaddedStride_Safe()
    {
        Assert.Throws<ArgumentNullException>(() => new LinearFramebuffer(null, 4, 4, 16));

        byte[] raw = new byte[128];
        fixed (byte* ptr = raw)
        {
            byte* p = ptr;
            Assert.Throws<ArgumentOutOfRangeException>(() => new LinearFramebuffer(p, 0, 4, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LinearFramebuffer(p, 4, 0, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LinearFramebuffer(p, 4, 4, stride: 8));

            // Padded stride: Width=4 (16 bytes), Stride=24 bytes, Height=4 -> 96 bytes
            using var paddedFb = new LinearFramebuffer(p, 4, 4, stride: 24);
            paddedFb.Clear(Rgba32.Cyan);

            Assert.Equal(Rgba32.Cyan, paddedFb.GetPixel(0, 0));
            Assert.Equal(Rgba32.Cyan, paddedFb.GetPixel(3, 3));

            Assert.Throws<InvalidOperationException>(() => paddedFb.AsRgbaSpan());
            Assert.Throws<InvalidOperationException>(() => paddedFb.AsBgraSpan());

            var span2d = paddedFb.AsReadOnlySpan2D();
            Assert.Equal(6, span2d.Stride);
            Assert.Equal(Rgba32.Cyan, span2d[3, 3]);
        }
    }

    [Fact]
    public void PngDecoder_RejectsCorruptCrcAndInvalidBounds()
    {
        using var fb = new LinearFramebuffer(8, 8);
        fb.Clear(Rgba32.Red);
        byte[] validPng = PngEncoder.Encode(fb);

        // Corrupt last byte of IEND CRC
        byte[] badCrc = (byte[])validPng.Clone();
        badCrc[^1] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(badCrc));

        // Corrupt IHDR CRC
        byte[] badIhdrCrc = (byte[])validPng.Clone();
        badIhdrCrc[8 + 4 + 13] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(badIhdrCrc));

        // Reject negative chunk length
        byte[] badLen = new byte[24];
        validPng.AsSpan(0, 8).CopyTo(badLen);
        BinaryPrimitives.WriteInt32BigEndian(badLen.AsSpan(8, 4), -5);
        Encoding.ASCII.GetBytes("IHDR", badLen.AsSpan(12, 4));
        Assert.Throws<FormatException>(() => PngDecoder.Decode(badLen));
    }

    [Fact]
    public void TrueTypeFont_MalformedRepeatCount_ThrowsInvalidDataException()
    {
        byte[] ttf = new byte[164];
        BinaryPrimitives.WriteUInt32BigEndian(ttf.AsSpan(0, 4), 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(ttf.AsSpan(4, 2), 4);

        static void WriteDirEntry(byte[] buf, int entryIndex, string tag, int offset, int length)
        {
            int pos = 12 + entryIndex * 16;
            Encoding.ASCII.GetBytes(tag, buf.AsSpan(pos, 4));
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos + 8, 4), offset);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(pos + 12, 4), length);
        }

        WriteDirEntry(ttf, 0, "head", 76, 54);
        WriteDirEntry(ttf, 1, "maxp", 130, 6);
        WriteDirEntry(ttf, 2, "loca", 136, 8);
        WriteDirEntry(ttf, 3, "glyf", 144, 20);

        BinaryPrimitives.WriteUInt16BigEndian(ttf.AsSpan(76 + 18, 2), 1000);
        BinaryPrimitives.WriteInt16BigEndian(ttf.AsSpan(76 + 50, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(ttf.AsSpan(130 + 4, 2), 1);
        BinaryPrimitives.WriteInt32BigEndian(ttf.AsSpan(136, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(ttf.AsSpan(136 + 4, 4), 20);

        BinaryPrimitives.WriteInt16BigEndian(ttf.AsSpan(144, 2), 1); // 1 contour
        BinaryPrimitives.WriteUInt16BigEndian(ttf.AsSpan(144 + 10, 2), 2); // endPt = 2 (3 points)
        BinaryPrimitives.WriteUInt16BigEndian(ttf.AsSpan(144 + 12, 2), 0); // instructionLength = 0
        ttf[144 + 14] = 0x09; // on-curve + repeat
        ttf[144 + 15] = 250;  // repeatCount overflows numPoints=3

        var font = new TrueTypeFont(ttf);
        Assert.Throws<InvalidDataException>(() => font.GetGlyphPath(0));
    }

    [Fact]
    public void PdfDocument_And_PdfPage_DisposeCleanlyAndEmitPdf17()
    {
        byte[] pdfBytes;
        using (var doc = new PdfDocument())
        using (var page = doc.AddPage(300, 200))
        using (var canvas = new PdfGraphicsCanvas(doc, page))
        {
            var rect = new VectorPath();
            rect.AddRect(10, 10, 50, 50);
            canvas.FillPath(rect, new Paint(Rgba32.Blue));
            pdfBytes = doc.Save();
        }

        string text = Encoding.Latin1.GetString(pdfBytes);
        Assert.StartsWith("%PDF-1.7", text);
        Assert.EndsWith("%%EOF\n", text);
    }

    [Fact]
    public void GlyphAtlas_ValueKeyedLru_HitsAcrossEquivalentFontsAndRetainsHotGlyphs()
    {
        // Small 64x64 atlas forces LRU eviction after several glyphs
        var atlas = new GlyphAtlas(64, 64);

        // Two distinct Font objects with identical value properties must hit the same cache entry
        var fontA = new Font(size: 16f);
        var fontB = new Font(size: 16f);

        AtlasGlyph g1 = atlas.GetOrAdd(fontA, 1, 0f);
        AtlasGlyph g2 = atlas.GetOrAdd(fontB, 1, 0f);
        Assert.Same(g1, g2);

        // Fill atlas past capacity with many glyph/size combinations to trigger LRU eviction
        for (int s = 10; s <= 40; s++)
        {
            // Touch glyph 1 with fontA repeatedly so it remains hot (MRU)
            atlas.GetOrAdd(fontA, 1, 0f);
            atlas.GetOrAdd(new Font(size: s), 1, 0f);
        }

        // Atlas should have retained warm entries (not wiped completely to 0)
        Assert.True(atlas.CachedGlyphCount > 1);
    }

    [Fact]
    public void PngFilters_AllFilterTypes_RoundtripLargeScanline()
    {
        const int len = 256;
        byte[] prior = new byte[len];
        byte[] original = new byte[len];
        for (int i = 0; i < len; i++)
        {
            prior[i] = (byte)((i * 37) & 0xFF);
            original[i] = (byte)((i * 53 + 11) & 0xFF);
        }

        for (byte filter = 0; filter <= 4; filter++)
        {
            byte[] filtered = new byte[len];
            byte[] reconstructed = new byte[len];
            PngFilters.ApplyFilter(filter, original, prior, filtered, bytesPerPixel: 4);
            filtered.CopyTo(reconstructed, 0);
            PngFilters.ReconstructScanline(filter, reconstructed, prior, bytesPerPixel: 4);
            Assert.Equal(original, reconstructed);
        }
    }

    [Fact]
    public void JpegColorTransform_ConvertBlock_MatchesScalarConvertPixel()
    {
        float[] y = new float[64];
        float[] cb = new float[64];
        float[] cr = new float[64];
        Rgba32[] vecOut = new Rgba32[64];

        for (int i = 0; i < 64; i++)
        {
            y[i] = (i * 4.0f) % 256f;
            cb[i] = 64f + (i * 2.0f);
            cr[i] = 192f - (i * 1.5f);
        }

        JpegColorTransform.ConvertBlock(y, cb, cr, vecOut);

        for (int i = 0; i < 64; i++)
        {
            Rgba32 expected = JpegColorTransform.ConvertPixel(y[i], cb[i], cr[i]);
            Assert.InRange(Math.Abs(vecOut[i].R - expected.R), 0, 1);
            Assert.InRange(Math.Abs(vecOut[i].G - expected.G), 0, 1);
            Assert.InRange(Math.Abs(vecOut[i].B - expected.B), 0, 1);
            Assert.Equal(255, vecOut[i].A);
        }
    }

    [Fact]
    public void CpuRasterizer_EvenOddAndNonZero_MatchesTileComputeRasterizer()
    {
        var path = new VectorPath();
        path.AddOval(8, 8, 48, 48);

        using var cpuFb = new LinearFramebuffer(64, 64);
        using var tileFb = new LinearFramebuffer(64, 64);

        new CpuRasterizer().FillPath(cpuFb, path, new Rgba32(40, 180, 220, 200), WindingRule.NonZero);
        new TileComputeRasterizer().Rasterize(tileFb, path, new Rgba32(40, 180, 220, 200), WindingRule.NonZero);

        var cpuCenter = cpuFb.GetPixel(32, 32);
        var tileCenter = tileFb.GetPixel(32, 32);
        Assert.InRange(Math.Abs(cpuCenter.R - tileCenter.R), 0, 2);
        Assert.InRange(Math.Abs(cpuCenter.G - tileCenter.G), 0, 2);
        Assert.InRange(Math.Abs(cpuCenter.B - tileCenter.B), 0, 2);
        Assert.InRange(Math.Abs(cpuCenter.A - tileCenter.A), 0, 2);
    }

    [Fact]
    public void CpuRasterizer_EvenOdd_PunchesHoleInConcentricRects()
    {
        var path = new VectorPath { FillRule = WindingRule.EvenOdd };
        path.AddRect(4, 4, 56, 56);
        path.AddRect(16, 16, 32, 32);

        using var fb = new LinearFramebuffer(64, 64);
        new CpuRasterizer().FillPath(fb, path, Rgba32.Red, WindingRule.EvenOdd);

        // Outer ring should be filled
        Assert.Equal(Rgba32.Red, fb.GetPixel(10, 10));
        // Inner hole should remain transparent
        Assert.Equal(0, fb.GetPixel(32, 32).A);
    }

    [Fact]
    public void JpegAanIdct_DenseBlock_MatchesExact2DIdct()
    {
        Span<float> coeffs = stackalloc float[64];
        for (int i = 0; i < 64; i++)
        {
            coeffs[i] = ((i * 17) % 41) - 20f;
        }

        Span<float> actual = stackalloc float[64];
        coeffs.CopyTo(actual);
        JpegAanIdct.Transform8x8(actual);

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                double sum = 0.0;
                for (int u = 0; u < 8; u++)
                {
                    double cu = u == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                    double cosY = Math.Cos((2 * y + 1) * u * Math.PI / 16.0);
                    for (int v = 0; v < 8; v++)
                    {
                        double cv = v == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0;
                        double cosX = Math.Cos((2 * x + 1) * v * Math.PI / 16.0);
                        sum += cu * cv * coeffs[u * 8 + v] * cosY * cosX;
                    }
                }
                double expected = 0.25 * sum;
                Assert.True(Math.Abs(expected - actual[y * 8 + x]) < 5e-2,
                    $"IDCT mismatch at ({y},{x}): expected={expected}, actual={actual[y * 8 + x]}");
            }
        }
    }

    [Fact]
    public void PolygonClipper_LargePolygon_UsesArrayPoolAndClipsAccurately()
    {
        const int vertexCount = 300; // Exceeds StackAllocThreshold (256)
        PointF[] subject = new PointF[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            float angle = (2f * MathF.PI * i) / vertexCount;
            subject[i] = new PointF(50f + 80f * MathF.Cos(angle), 50f + 80f * MathF.Sin(angle));
        }

        PointF[] clipSquare =
        [
            new(0f, 0f),
            new(100f, 0f),
            new(100f, 100f),
            new(0f, 100f)
        ];

        PointF[] output = new PointF[vertexCount * 2];
        int clipped = PolygonClipper.ClipConvex(subject, clipSquare, output);

        Assert.True(clipped >= 4);
        for (int i = 0; i < clipped; i++)
        {
            Assert.InRange(output[i].X, -0.01f, 100.01f);
            Assert.InRange(output[i].Y, -0.01f, 100.01f);
        }
    }

    [Fact]
    public void LineStroker_ClosedRoundedDashedPaths_ProduceValidGeometry()
    {
        var oval = new VectorPath();
        oval.AddOval(10, 10, 80, 60);

        VectorPath solidClosed = LineStroker.Stroke(oval, strokeWidth: 4f, join: StrokeJoin.Round);
        Assert.True(solidClosed.VerbCount > 8);

        var line = new VectorPath();
        line.MoveTo(0, 0);
        line.LineTo(120, 0);
        float[] pattern = [12f, 6f];
        VectorPath dashed = LineStroker.Stroke(line, strokeWidth: 3f, cap: StrokeCap.Round, dashPattern: pattern);
        Assert.True(dashed.VerbCount > 12);
    }

    [Fact]
    public void CpuGraphicsCanvas_DisjointClipPath_SuppressesAllDrawingAndRestoresCleanly()
    {
        using var canvas = new CpuGraphicsCanvas(32, 32);
        canvas.Clear(Rgba32.Black);
        canvas.Save();

        var clipA = new VectorPath();
        clipA.AddRect(0, 0, 10, 10);
        var clipB = new VectorPath();
        clipB.AddRect(20, 20, 10, 10);

        canvas.ClipPath(clipA);
        canvas.ClipPath(clipB); // Disjoint intersection -> empty clip

        var fullRect = new VectorPath();
        fullRect.AddRect(0, 0, 32, 32);

        canvas.FillPath(fullRect, new Paint(Rgba32.Red));
        canvas.DrawPath(fullRect, new Paint(Rgba32.Green, PaintStyle.Stroke, StrokeWidth: 4f));
        canvas.DrawText("X", 4f, 20f, new Font(16f), new Paint(Rgba32.Blue));

        using var srcImg = new LinearFramebuffer(8, 8);
        srcImg.Clear(Rgba32.White);
        canvas.DrawImage(srcImg.AsReadOnlySpan2D(), 0, 0, 32, 32);

        foreach (var px in canvas.Framebuffer.AsRgbaSpan())
        {
            Assert.Equal(Rgba32.Black, px);
        }

        // Restore must recover the outer clip
        canvas.Restore();
        canvas.FillPath(fullRect, new Paint(Rgba32.Red));
        Assert.Equal(Rgba32.Red, canvas.Framebuffer.GetPixel(15, 15));

        // Direct CpuRasterizer calls with explicit empty clip must also draw nothing
        using var rasterFb = new LinearFramebuffer(16, 16);
        rasterFb.Clear(Rgba32.Black);
        var rasterizer = new CpuRasterizer();
        rasterizer.FillPath(rasterFb, fullRect, Rgba32.Red, WindingRule.NonZero, (RectF?)RectF.Empty);
        rasterizer.StrokePath(rasterFb, fullRect, new Paint(Rgba32.Green, PaintStyle.Stroke, StrokeWidth: 3f), (RectF?)RectF.Empty);
        foreach (var px in rasterFb.AsRgbaSpan())
        {
            Assert.Equal(Rgba32.Black, px);
        }
    }

    [Fact]
    public void TrueTypeFont_NegativeTableAndCmapSubtableOffsets_ThrowInvalidDataException()
    {
        // Case 1: Negative SFNT table offset in directory
        byte[] negTableTtf = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(negTableTtf.AsSpan(0, 4), 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(negTableTtf.AsSpan(4, 2), 1);
        Encoding.ASCII.GetBytes("head", negTableTtf.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(negTableTtf.AsSpan(12 + 8, 4), -100);
        BinaryPrimitives.WriteInt32BigEndian(negTableTtf.AsSpan(12 + 12, 4), 54);
        Assert.Throws<InvalidDataException>(() => new TrueTypeFont(negTableTtf));

        // Case 2: Out-of-bounds table length in directory
        byte[] oobLenTtf = (byte[])negTableTtf.Clone();
        BinaryPrimitives.WriteInt32BigEndian(oobLenTtf.AsSpan(12 + 8, 4), 28);
        BinaryPrimitives.WriteInt32BigEndian(oobLenTtf.AsSpan(12 + 12, 4), 1024);
        Assert.Throws<InvalidDataException>(() => new TrueTypeFont(oobLenTtf));

        // Case 3: Negative cmap subtable offset
        byte[] negCmapSubTtf = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(negCmapSubTtf.AsSpan(0, 4), 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(negCmapSubTtf.AsSpan(4, 2), 1);
        Encoding.ASCII.GetBytes("cmap", negCmapSubTtf.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(negCmapSubTtf.AsSpan(12 + 8, 4), 28);
        BinaryPrimitives.WriteInt32BigEndian(negCmapSubTtf.AsSpan(12 + 12, 4), 16);
        BinaryPrimitives.WriteUInt16BigEndian(negCmapSubTtf.AsSpan(28, 2), 0); // cmap version
        BinaryPrimitives.WriteUInt16BigEndian(negCmapSubTtf.AsSpan(30, 2), 1); // numSubtables
        BinaryPrimitives.WriteUInt16BigEndian(negCmapSubTtf.AsSpan(32, 2), 3); // Windows
        BinaryPrimitives.WriteUInt16BigEndian(negCmapSubTtf.AsSpan(34, 2), 1); // Unicode BMP
        BinaryPrimitives.WriteInt32BigEndian(negCmapSubTtf.AsSpan(36, 4), -100); // Negative subtable offset
        Assert.Throws<InvalidDataException>(() => new TrueTypeFont(negCmapSubTtf));
    }

    [Fact]
    public void PdfDocument_SaveToStreamAtNonZeroPosition_EmitsZeroBasedXrefOffsets()
    {
        const int prefixBytes = 100;
        using var ms = new MemoryStream();
        ms.Write(new byte[prefixBytes]);

        using (var doc = new PdfDocument())
        using (var page = doc.AddPage(300, 200))
        using (var canvas = new PdfGraphicsCanvas(doc, page))
        {
            var rect = new VectorPath();
            rect.AddRect(10, 10, 50, 50);
            canvas.FillPath(rect, new Paint(Rgba32.Red));
            doc.Save(ms);
        }

        byte[] allBytes = ms.ToArray();
        byte[] pdfSlice = allBytes.AsSpan(prefixBytes).ToArray();
        string pdfText = Encoding.Latin1.GetString(pdfSlice);

        Assert.StartsWith("%PDF-1.7", pdfText);

        int startxrefMarker = pdfText.LastIndexOf("startxref\n", StringComparison.Ordinal);
        Assert.True(startxrefMarker > 0);
        int offsetStart = startxrefMarker + "startxref\n".Length;
        int offsetEnd = pdfText.IndexOf('\n', offsetStart);
        long xrefOffset = long.Parse(pdfText.AsSpan(offsetStart, offsetEnd - offsetStart), CultureInfo.InvariantCulture);

        Assert.InRange(xrefOffset, 0, pdfSlice.Length - 5);
        Assert.Equal("xref\n", Encoding.Latin1.GetString(pdfSlice, (int)xrefOffset, 5));

        string xrefSection = pdfText.Substring((int)xrefOffset);
        string[] lines = xrefSection.Split('\n');
        string[] headerParts = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int objCount = int.Parse(headerParts[1], CultureInfo.InvariantCulture);

        for (int objId = 1; objId < objCount; objId++)
        {
            string entryLine = lines[2 + objId];
            Assert.EndsWith(" 00000 n ", entryLine);
            long objOffset = long.Parse(entryLine.AsSpan(0, 10), CultureInfo.InvariantCulture);
            string expectedPrefix = $"{objId} 0 obj\n";
            Assert.Equal(expectedPrefix, Encoding.Latin1.GetString(pdfSlice, (int)objOffset, expectedPrefix.Length));
        }
    }

    [Fact]
    public void PngDecoder_IdatBeforeIhdr_ThrowsInvalidDataException()
    {
        using var fb = new LinearFramebuffer(2, 2);
        fb.Clear(Rgba32.Blue);
        byte[] validPng = PngEncoder.Encode(fb);

        byte[]? ihdrChunk = null;
        byte[]? idatChunk = null;
        byte[]? iendChunk = null;

        int pos = 8;
        while (pos + 12 <= validPng.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(validPng.AsSpan(pos, 4));
            int totalChunkLen = 12 + len;
            string type = Encoding.ASCII.GetString(validPng, pos + 4, 4);
            byte[] chunk = validPng.AsSpan(pos, totalChunkLen).ToArray();
            if (type == "IHDR") ihdrChunk = chunk;
            else if (type == "IDAT") idatChunk = chunk;
            else if (type == "IEND") iendChunk = chunk;
            pos += totalChunkLen;
        }

        Assert.NotNull(ihdrChunk);
        Assert.NotNull(idatChunk);
        Assert.NotNull(iendChunk);

        using var ms = new MemoryStream();
        ms.Write(validPng, 0, 8);
        ms.Write(idatChunk!);
        ms.Write(ihdrChunk!);
        ms.Write(iendChunk!);

        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(ms.ToArray()));
    }
}
