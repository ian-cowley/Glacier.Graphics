namespace Glacier.Graphics.Tests;

using System;
using System.Buffers.Binary;
using System.IO;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class TrueTypeFontTests
{
    [Fact]
    public void TrueTypeParser_ParsesSyntheticFont()
    {
        byte[] fontBytes = CreateMinimalTrueTypeFont();
        var font = new TrueTypeFont(fontBytes);

        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(800, font.Ascender);
        Assert.Equal(-200, font.Descender);
        Assert.Equal(2, font.NumGlyphs);

        int glyphA = font.GetGlyphIndex('A');
        Assert.Equal(1, glyphA);

        ushort advance = font.GetAdvanceWidth(glyphA);
        Assert.Equal(600, advance);

        VectorPath path = font.GetGlyphPath(glyphA);
        Assert.True(path.PointCount > 0);
    }

    [Fact]
    public void TrueTypeParser_ParsesWindowsSystemFontIfPresent()
    {
        string arialPath = @"C:\Windows\Fonts\arial.ttf";
        if (File.Exists(arialPath))
        {
            byte[] bytes = File.ReadAllBytes(arialPath);
            var font = new TrueTypeFont(bytes);

            Assert.True(font.UnitsPerEm > 0);
            Assert.True(font.NumGlyphs > 100);

            int glyphA = font.GetGlyphIndex('A');
            Assert.True(glyphA > 0);

            ushort advance = font.GetAdvanceWidth(glyphA);
            Assert.True(advance > 0);

            VectorPath path = font.GetGlyphPath(glyphA);
            Assert.True(path.PointCount > 0);
        }
    }

    /// <summary>
    /// Programmatically generates a compliant minimal SFNT TrueType binary with 2 glyphs (.notdef and 'A').
    /// </summary>
    private static byte[] CreateMinimalTrueTypeFont()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // 7 tables: head, hhea, maxp, hmtx, cmap, loca, glyf
        ushort numTables = 7;
        writer.Write(new byte[] { 0x00, 0x01, 0x00, 0x00 }); // sfnt version 1.0
        WriteUInt16BE(writer, numTables);
        WriteUInt16BE(writer, 64); // searchRange
        WriteUInt16BE(writer, 2);  // entrySelector
        WriteUInt16BE(writer, 48); // rangeShift

        // Table payloads
        byte[] headTable = CreateHeadTable();
        byte[] hheaTable = CreateHheaTable();
        byte[] maxpTable = CreateMaxpTable();
        byte[] hmtxTable = CreateHmtxTable();
        byte[] cmapTable = CreateCmapTable();
        byte[] glyfTable = CreateGlyfTable(out byte[] locaTable);

        var tables = new (string Tag, byte[] Data)[]
        {
            ("cmap", cmapTable),
            ("glyf", glyfTable),
            ("head", headTable),
            ("hhea", hheaTable),
            ("hmtx", hmtxTable),
            ("loca", locaTable),
            ("maxp", maxpTable)
        };

        // Table directory offset: 12 + 7 * 16 = 124
        int currentOffset = 12 + numTables * 16;
        foreach (var t in tables)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(t.Tag));
            WriteUInt32BE(writer, 0); // Checksum
            WriteUInt32BE(writer, (uint)currentOffset);
            WriteUInt32BE(writer, (uint)t.Data.Length);
            currentOffset += (t.Data.Length + 3) & ~3; // 4-byte aligned
        }

        // Write table datas
        foreach (var t in tables)
        {
            writer.Write(t.Data);
            int pad = ((t.Data.Length + 3) & ~3) - t.Data.Length;
            for (int p = 0; p < pad; p++) writer.Write((byte)0);
        }

        return ms.ToArray();
    }

    private static byte[] CreateHeadTable()
    {
        byte[] b = new byte[54];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0, 4), 0x00010000); // Version 1.0
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18, 2), 1000);      // unitsPerEm
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(50, 2), 1);          // indexToLocFormat = 1 (long)
        return b;
    }

    private static byte[] CreateHheaTable()
    {
        byte[] b = new byte[36];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(4, 2), 800);   // Ascender
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6, 2), -200);  // Descender
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(8, 2), 0);     // LineGap
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(34, 2), 2);   // numberOfHMetrics = 2
        return b;
    }

    private static byte[] CreateMaxpTable()
    {
        byte[] b = new byte[32];
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 2);    // numGlyphs = 2
        return b;
    }

    private static byte[] CreateHmtxTable()
    {
        byte[] b = new byte[8];
        // Glyph 0 (.notdef): advance 500, lsb 0
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(0, 2), 500);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(2, 2), 0);
        // Glyph 1 ('A'): advance 600, lsb 50
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4, 2), 600);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6, 2), 50);
        return b;
    }

    private static byte[] CreateCmapTable()
    {
        // cmap with 1 subtable (Format 4, Unicode)
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        WriteUInt16BE(w, 0); // version
        WriteUInt16BE(w, 1); // numSubtables

        WriteUInt16BE(w, 3); // platformId Windows
        WriteUInt16BE(w, 1); // encodingId Unicode BMP
        WriteUInt32BE(w, 12); // subtable offset

        // Format 4 subtable
        // Maps 'A' (65) -> Glyph 1
        // Segments: 2 segments: [65, 65] and [0xFFFF, 0xFFFF]
        WriteUInt16BE(w, 4);  // format 4
        WriteUInt16BE(w, 32); // length
        WriteUInt16BE(w, 0);  // language
        WriteUInt16BE(w, 4);  // segCountX2 (2 segments * 2)
        WriteUInt16BE(w, 4);  // searchRange
        WriteUInt16BE(w, 1);  // entrySelector
        WriteUInt16BE(w, 0);  // rangeShift

        // endCode
        WriteUInt16BE(w, 65);
        WriteUInt16BE(w, 0xFFFF);
        WriteUInt16BE(w, 0); // reservedPad

        // startCode
        WriteUInt16BE(w, 65);
        WriteUInt16BE(w, 0xFFFF);

        // idDelta
        WriteUInt16BE(w, unchecked((ushort)(1 - 65))); // 65 + (1 - 65) = 1
        WriteUInt16BE(w, 1);

        // idRangeOffset
        WriteUInt16BE(w, 0);
        WriteUInt16BE(w, 0);

        return ms.ToArray();
    }

    private static byte[] CreateGlyfTable(out byte[] locaTable)
    {
        // Glyph 0: empty (.notdef)
        // Glyph 1: simple triangle for 'A' (1 contour, 3 points)
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // Glyph 1:
        // numberOfContours = 1, xMin=0, yMin=0, xMax=600, yMax=800
        WriteInt16BE(w, 1);   // numberOfContours

        WriteInt16BE(w, 0);   // xMin
        WriteInt16BE(w, 0);   // yMin
        WriteInt16BE(w, 600); // xMax
        WriteInt16BE(w, 800); // yMax

        WriteUInt16BE(w, 2); // endPtsOfContours[0] = index 2 (3 points: 0, 1, 2)
        WriteUInt16BE(w, 0); // instructionLength = 0

        // Flags for 3 points: 0x01 (on-curve), 0x01, 0x01
        w.Write((byte)0x01);
        w.Write((byte)0x01);
        w.Write((byte)0x01);

        // X coords: 0, 300, 600
        WriteInt16BE(w, 0);
        WriteInt16BE(w, 300);
        WriteInt16BE(w, 300);

        // Y coords: 0, 800, 0
        WriteInt16BE(w, 0);
        WriteInt16BE(w, 800);
        WriteInt16BE(w, -800);

        byte[] glyf = ms.ToArray();

        // Loca table (format 1 = 32-bit offsets): 3 offsets for 2 glyphs
        byte[] loca = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(loca.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(loca.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(loca.AsSpan(8, 4), glyf.Length);
        locaTable = loca;

        return glyf;
    }

    private static void WriteUInt16BE(BinaryWriter w, ushort val)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, val);
        w.Write(b);
    }

    private static void WriteInt16BE(BinaryWriter w, short val)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, val);
        w.Write(b);
    }

    private static void WriteUInt32BE(BinaryWriter w, uint val)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, val);
        w.Write(b);
    }
}
