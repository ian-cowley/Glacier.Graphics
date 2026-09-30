namespace Glacier.Graphics.Text;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Glacier.Graphics.Vector;

/// <summary>
/// Parsed TrueType / OpenType font file.
/// Provides zero-copy table navigation, cmap 4 &amp; 12 Unicode mapping, typography metrics,
/// and TrueType quadratic outline extraction directly into VectorPath.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _fontData;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);

    public ushort UnitsPerEm { get; private set; } = 1000;
    public short Ascender { get; private set; }
    public short Descender { get; private set; }
    public short LineGap { get; private set; }
    public ushort NumberOfHMetrics { get; private set; }
    public ushort NumGlyphs { get; private set; }
    public short IndexToLocFormat { get; private set; } // 0 = 16-bit offset / 2, 1 = 32-bit offset

    private int _cmapSubtableOffset = -1;
    private int _cmapFormat = -1;
    private int _locaOffset = -1;
    private int _glyfOffset = -1;

    public bool HasGlyfTable => _glyfOffset >= 0;

    public TrueTypeFont(byte[] data)
    {
        _fontData = data ?? throw new ArgumentNullException(nameof(data));
        ParseFont();
    }

    public TrueTypeFont(ReadOnlySpan<byte> data)
    {
        _fontData = data.ToArray();
        ParseFont();
    }

    private void ParseFont()
    {
        if (_fontData.Length < 12)
            throw new FormatException("Font data is too small to be a valid SFNT container.");

        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(4, 2));
        int offset = 12;

        for (int i = 0; i < numTables && offset + 16 <= _fontData.Length; i++)
        {
            string tag = System.Text.Encoding.ASCII.GetString(_fontData, offset, 4);
            int tableOffset = BinaryPrimitives.ReadInt32BigEndian(_fontData.AsSpan(offset + 8, 4));
            int tableLength = BinaryPrimitives.ReadInt32BigEndian(_fontData.AsSpan(offset + 12, 4));
            _tables[tag] = (tableOffset, tableLength);
            offset += 16;
        }

        ParseHeadTable();
        ParseMaxpTable();
        ParseHheaTable();
        ParseCmapTable();

        if (_tables.TryGetValue("loca", out var locaEntry)) _locaOffset = locaEntry.Offset;
        if (_tables.TryGetValue("glyf", out var glyfEntry)) _glyfOffset = glyfEntry.Offset;
    }

    private void ParseHeadTable()
    {
        if (!_tables.TryGetValue("head", out var entry) || entry.Offset + 54 > _fontData.Length)
            return;

        UnitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(entry.Offset + 18, 2));
        if (UnitsPerEm == 0) UnitsPerEm = 1000;
        IndexToLocFormat = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(entry.Offset + 50, 2));
    }

    private void ParseMaxpTable()
    {
        if (!_tables.TryGetValue("maxp", out var entry) || entry.Offset + 6 > _fontData.Length)
            return;

        NumGlyphs = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(entry.Offset + 4, 2));
    }

    private void ParseHheaTable()
    {
        if (!_tables.TryGetValue("hhea", out var entry) || entry.Offset + 36 > _fontData.Length)
            return;

        Ascender = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(entry.Offset + 4, 2));
        Descender = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(entry.Offset + 6, 2));
        LineGap = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(entry.Offset + 8, 2));
        NumberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(entry.Offset + 34, 2));
    }

    private void ParseCmapTable()
    {
        if (!_tables.TryGetValue("cmap", out var entry) || entry.Offset + 4 > _fontData.Length)
            return;

        int cmapStart = entry.Offset;
        ushort numSubtables = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(cmapStart + 2, 2));

        int bestOffset = -1;
        int bestFormat = -1;

        for (int i = 0; i < numSubtables; i++)
        {
            int recOffset = cmapStart + 4 + (i * 8);
            if (recOffset + 8 > _fontData.Length) break;

            ushort platformId = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(recOffset, 2));
            ushort encodingId = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(recOffset + 2, 2));
            int subtableOffset = cmapStart + BinaryPrimitives.ReadInt32BigEndian(_fontData.AsSpan(recOffset + 4, 4));

            if (subtableOffset + 2 > _fontData.Length) continue;
            ushort format = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(subtableOffset, 2));

            if (format == 4 && (platformId == 0 || (platformId == 3 && encodingId == 1)))
            {
                bestOffset = subtableOffset;
                bestFormat = 4;
            }
            else if (format == 12 && (platformId == 0 || (platformId == 3 && encodingId == 10)))
            {
                bestOffset = subtableOffset;
                bestFormat = 12;
                break;
            }
        }

        _cmapSubtableOffset = bestOffset;
        _cmapFormat = bestFormat;
    }

    public int GetGlyphIndex(int unicodeCodePoint)
    {
        if (_cmapSubtableOffset < 0 || _cmapSubtableOffset >= _fontData.Length)
            return 0;

        if (_cmapFormat == 4 && unicodeCodePoint <= 0xFFFF)
        {
            return LookupFormat4((ushort)unicodeCodePoint);
        }
        else if (_cmapFormat == 12)
        {
            return LookupFormat12((uint)unicodeCodePoint);
        }

        return 0;
    }

    private int LookupFormat4(ushort codePoint)
    {
        int offset = _cmapSubtableOffset;
        if (offset + 14 > _fontData.Length) return 0;

        ushort segCountX2 = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(offset + 6, 2));
        int segCount = segCountX2 / 2;

        int endCodeOffset = offset + 14;
        int startCodeOffset = endCodeOffset + segCountX2 + 2;
        int idDeltaOffset = startCodeOffset + segCountX2;
        int idRangeOffsetTable = idDeltaOffset + segCountX2;

        if (idRangeOffsetTable + segCountX2 > _fontData.Length) return 0;

        for (int i = 0; i < segCount; i++)
        {
            ushort endCode = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(endCodeOffset + (i * 2), 2));
            if (endCode >= codePoint)
            {
                ushort startCode = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(startCodeOffset + (i * 2), 2));
                if (startCode <= codePoint)
                {
                    short idDelta = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(idDeltaOffset + (i * 2), 2));
                    int idRangeOffsetAddr = idRangeOffsetTable + (i * 2);
                    ushort idRangeOffset = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(idRangeOffsetAddr, 2));

                    if (idRangeOffset == 0)
                    {
                        return (ushort)((codePoint + idDelta) & 0xFFFF);
                    }
                    else
                    {
                        int glyphIndexAddr = idRangeOffsetAddr + idRangeOffset + ((codePoint - startCode) * 2);
                        if (glyphIndexAddr + 2 <= _fontData.Length)
                        {
                            ushort glyphIndex = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(glyphIndexAddr, 2));
                            if (glyphIndex != 0)
                            {
                                return (ushort)((glyphIndex + idDelta) & 0xFFFF);
                            }
                        }
                    }
                }
                break;
            }
        }

        return 0;
    }

    private int LookupFormat12(uint codePoint)
    {
        int offset = _cmapSubtableOffset;
        if (offset + 16 > _fontData.Length) return 0;

        uint numGroups = BinaryPrimitives.ReadUInt32BigEndian(_fontData.AsSpan(offset + 12, 4));
        int groupsStart = offset + 16;

        int low = 0;
        int high = (int)numGroups - 1;

        while (low <= high)
        {
            int mid = (low + high) / 2;
            int groupOffset = groupsStart + (mid * 12);
            if (groupOffset + 12 > _fontData.Length) break;

            uint startCharCode = BinaryPrimitives.ReadUInt32BigEndian(_fontData.AsSpan(groupOffset, 4));
            uint endCharCode = BinaryPrimitives.ReadUInt32BigEndian(_fontData.AsSpan(groupOffset + 4, 4));
            uint startGlyphId = BinaryPrimitives.ReadUInt32BigEndian(_fontData.AsSpan(groupOffset + 8, 4));

            if (codePoint < startCharCode) high = mid - 1;
            else if (codePoint > endCharCode) low = mid + 1;
            else return (int)(startGlyphId + (codePoint - startCharCode));
        }

        return 0;
    }

    public ushort GetAdvanceWidth(int glyphIndex)
    {
        if (!_tables.TryGetValue("hmtx", out var entry) || NumberOfHMetrics == 0)
            return UnitsPerEm;

        int hmtxOffset = entry.Offset;
        if (glyphIndex < NumberOfHMetrics)
        {
            int metricOffset = hmtxOffset + (glyphIndex * 4);
            if (metricOffset + 2 <= _fontData.Length)
                return BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(metricOffset, 2));
        }
        else
        {
            int lastMetricOffset = hmtxOffset + ((NumberOfHMetrics - 1) * 4);
            if (lastMetricOffset + 2 <= _fontData.Length)
                return BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(lastMetricOffset, 2));
        }

        return UnitsPerEm;
    }

    /// <summary>
    /// Gets byte offset to glyph record inside glyf table using loca table.
    /// </summary>
    public int GetGlyphOffset(int glyphIndex, out int length)
    {
        length = 0;
        if (_locaOffset < 0 || glyphIndex < 0 || glyphIndex >= NumGlyphs) return -1;

        int offset1, offset2;
        if (IndexToLocFormat == 0)
        {
            int addr = _locaOffset + (glyphIndex * 2);
            if (addr + 4 > _fontData.Length) return -1;
            offset1 = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(addr, 2)) * 2;
            offset2 = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(addr + 2, 2)) * 2;
        }
        else
        {
            int addr = _locaOffset + (glyphIndex * 4);
            if (addr + 8 > _fontData.Length) return -1;
            offset1 = BinaryPrimitives.ReadInt32BigEndian(_fontData.AsSpan(addr, 4));
            offset2 = BinaryPrimitives.ReadInt32BigEndian(_fontData.AsSpan(addr + 4, 4));
        }

        length = offset2 - offset1;
        if (length <= 0) return -1;
        return _glyfOffset + offset1;
    }

    /// <summary>
    /// Extracts TrueType glyph outline into a VectorPath.
    /// Supports simple glyphs (quadratic B-splines) and composite glyphs.
    /// </summary>
    public VectorPath GetGlyphPath(int glyphIndex)
    {
        var path = new VectorPath();
        ExtractGlyphPathRecursive(glyphIndex, path, Matrix3x2.Identity, 0);
        return path;
    }

    private void ExtractGlyphPathRecursive(int glyphIndex, VectorPath path, in Matrix3x2 transform, int depth)
    {
        if (depth > 10) return; // Prevent infinite recursion on circular composites

        int glyphOffset = GetGlyphOffset(glyphIndex, out int glyphLength);
        if (glyphOffset < 0 || glyphOffset + 10 > _fontData.Length) return;

        short numberOfContours = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(glyphOffset, 2));

        if (numberOfContours > 0)
        {
            ParseSimpleGlyph(glyphOffset, numberOfContours, path, transform);
        }
        else if (numberOfContours < 0)
        {
            ParseCompositeGlyph(glyphOffset, path, transform, depth);
        }
    }

    private void ParseSimpleGlyph(int offset, int numberOfContours, VectorPath path, in Matrix3x2 transform)
    {
        int readPos = offset + 10;
        ushort[] endPtsOfContours = new ushort[numberOfContours];
        for (int i = 0; i < numberOfContours; i++)
        {
            endPtsOfContours[i] = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(readPos, 2));
            readPos += 2;
        }

        int numPoints = endPtsOfContours[^1] + 1;
        ushort instructionLength = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(readPos, 2));
        readPos += 2 + instructionLength; // Skip hinting instructions

        // Read flags
        byte[] flags = new byte[numPoints];
        for (int i = 0; i < numPoints; i++)
        {
            byte flag = _fontData[readPos++];
            flags[i] = flag;
            if ((flag & 0x08) != 0) // Repeat flag
            {
                byte repeatCount = _fontData[readPos++];
                for (int r = 0; r < repeatCount; r++)
                {
                    flags[++i] = flag;
                }
            }
        }

        // Read X coordinates
        short[] xCoords = new short[numPoints];
        short currentX = 0;
        for (int i = 0; i < numPoints; i++)
        {
            byte flag = flags[i];
            bool isShort = (flag & 0x02) != 0;
            bool sameOrPos = (flag & 0x10) != 0;

            if (isShort)
            {
                byte val = _fontData[readPos++];
                currentX += (short)(sameOrPos ? val : -val);
            }
            else if (!sameOrPos)
            {
                short val = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos, 2));
                readPos += 2;
                currentX += val;
            }
            xCoords[i] = currentX;
        }

        // Read Y coordinates
        short[] yCoords = new short[numPoints];
        short currentY = 0;
        for (int i = 0; i < numPoints; i++)
        {
            byte flag = flags[i];
            bool isShort = (flag & 0x04) != 0;
            bool sameOrPos = (flag & 0x20) != 0;

            if (isShort)
            {
                byte val = _fontData[readPos++];
                currentY += (short)(sameOrPos ? val : -val);
            }
            else if (!sameOrPos)
            {
                short val = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos, 2));
                readPos += 2;
                currentY += val;
            }
            yCoords[i] = currentY;
        }

        // Build contour paths
        int startPt = 0;
        for (int c = 0; c < numberOfContours; c++)
        {
            int endPt = endPtsOfContours[c];
            int count = endPt - startPt + 1;
            if (count > 0)
            {
                BuildContour(path, xCoords.AsSpan(startPt, count), yCoords.AsSpan(startPt, count), flags.AsSpan(startPt, count), transform);
            }
            startPt = endPt + 1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PointF GetPoint(ReadOnlySpan<short> xCoords, ReadOnlySpan<short> yCoords, in Matrix3x2 transform, int idx)
    {
        PointF p = new PointF(xCoords[idx], yCoords[idx]);
        return Matrix3x2.TransformPoint(transform, p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOnCurve(ReadOnlySpan<byte> flags, int idx) => (flags[idx] & 0x01) != 0;

    private static void BuildContour(
        VectorPath path,
        ReadOnlySpan<short> xCoords,
        ReadOnlySpan<short> yCoords,
        ReadOnlySpan<byte> flags,
        in Matrix3x2 transform)
    {
        int n = xCoords.Length;
        if (n == 0) return;

        int startIndex = 0;
        PointF startPoint;

        if (IsOnCurve(flags, 0))
        {
            startPoint = GetPoint(xCoords, yCoords, transform, 0);
        }
        else if (IsOnCurve(flags, n - 1))
        {
            startPoint = GetPoint(xCoords, yCoords, transform, n - 1);
            startIndex = 0;
        }
        else
        {
            // Both first and last are off-curve: start at midpoint
            startPoint = (GetPoint(xCoords, yCoords, transform, 0) + GetPoint(xCoords, yCoords, transform, n - 1)) * 0.5f;
        }

        path.MoveTo(startPoint);

        int i = startIndex;
        int end = startIndex + n;

        while (i < end)
        {
            int currIdx = i % n;
            int nextIdx = (i + 1) % n;

            if (IsOnCurve(flags, currIdx))
            {
                if (IsOnCurve(flags, nextIdx))
                {
                    path.LineTo(GetPoint(xCoords, yCoords, transform, nextIdx));
                    i++;
                }
                else
                {
                    // Next is off-curve
                    PointF c = GetPoint(xCoords, yCoords, transform, nextIdx);
                    int afterNext = (i + 2) % n;
                    if (IsOnCurve(flags, afterNext))
                    {
                        path.QuadTo(c, GetPoint(xCoords, yCoords, transform, afterNext));
                        i += 2;
                    }
                    else
                    {
                        // Two off-curve points in a row: synthesize midpoint
                        PointF mid = (c + GetPoint(xCoords, yCoords, transform, afterNext)) * 0.5f;
                        path.QuadTo(c, mid);
                        i++;
                    }
                }
            }
            else
            {
                // Current is off-curve
                PointF c = GetPoint(xCoords, yCoords, transform, currIdx);
                if (IsOnCurve(flags, nextIdx))
                {
                    path.QuadTo(c, GetPoint(xCoords, yCoords, transform, nextIdx));
                    i++;
                }
                else
                {
                    PointF mid = (c + GetPoint(xCoords, yCoords, transform, nextIdx)) * 0.5f;
                    path.QuadTo(c, mid);
                    i++;
                }
            }
        }


        path.Close();
    }

    private void ParseCompositeGlyph(int offset, VectorPath path, in Matrix3x2 transform, int depth)
    {
        int readPos = offset + 10;
        const ushort ARG_1_AND_2_ARE_WORDS = 0x0001;
        const ushort ARGS_ARE_XY_VALUES = 0x0002;
        const ushort WE_HAVE_A_SCALE = 0x0008;
        const ushort MORE_COMPONENTS = 0x0020;
        const ushort WE_HAVE_AN_X_AND_Y_SCALE = 0x0040;
        const ushort WE_HAVE_A_TWO_BY_TWO = 0x0080;

        ushort flags;
        do
        {
            flags = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(readPos, 2));
            ushort compGlyphIndex = BinaryPrimitives.ReadUInt16BigEndian(_fontData.AsSpan(readPos + 2, 2));
            readPos += 4;

            float m11 = 1f, m12 = 0f, m21 = 0f, m22 = 1f;
            float dx = 0f, dy = 0f;

            if ((flags & ARGS_ARE_XY_VALUES) != 0)
            {
                if ((flags & ARG_1_AND_2_ARE_WORDS) != 0)
                {
                    dx = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos, 2));
                    dy = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos + 2, 2));
                    readPos += 4;
                }
                else
                {
                    dx = (sbyte)_fontData[readPos++];
                    dy = (sbyte)_fontData[readPos++];
                }
            }
            else
            {
                // Point matching offsets
                readPos += (flags & ARG_1_AND_2_ARE_WORDS) != 0 ? 4 : 2;
            }

            if ((flags & WE_HAVE_A_SCALE) != 0)
            {
                float scale = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos, 2)) / 16384f;
                readPos += 2;
                m11 = scale;
                m22 = scale;
            }
            else if ((flags & WE_HAVE_AN_X_AND_Y_SCALE) != 0)
            {
                m11 = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos, 2)) / 16384f;
                m22 = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos + 2, 2)) / 16384f;
                readPos += 4;
            }
            else if ((flags & WE_HAVE_A_TWO_BY_TWO) != 0)
            {
                m11 = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos, 2)) / 16384f;
                m12 = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos + 2, 2)) / 16384f;
                m21 = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos + 4, 2)) / 16384f;
                m22 = BinaryPrimitives.ReadInt16BigEndian(_fontData.AsSpan(readPos + 6, 2)) / 16384f;
                readPos += 8;
            }

            Matrix3x2 compTransform = new Matrix3x2(m11, m12, m21, m22, dx, dy);
            Matrix3x2 combined = Matrix3x2.Multiply(compTransform, transform);

            ExtractGlyphPathRecursive(compGlyphIndex, path, combined, depth + 1);

        } while ((flags & MORE_COMPONENTS) != 0);
    }

    public ReadOnlySpan<byte> GetTableData(string tag)
    {
        if (_tables.TryGetValue(tag, out var entry))
        {
            return _fontData.AsSpan(entry.Offset, entry.Length);
        }
        return default;
    }

    public ReadOnlyMemory<byte> FontData => _fontData;
}
