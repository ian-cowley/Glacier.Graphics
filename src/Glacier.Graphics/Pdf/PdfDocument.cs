namespace Glacier.Graphics.Pdf;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Glacier.Graphics.Text;

/// <summary>
/// PDF blend modes for ExtGState alpha compositing.
/// </summary>
public enum PdfBlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion
}

/// <summary>
/// Single page within a vector PDF document.
/// </summary>
public sealed class PdfPage : IDisposable
{
    private readonly PooledBufferWriter _contentWriter = new();
    private readonly Dictionary<string, (float Alpha, PdfBlendMode Mode)> _extGStates = new();
    private bool _disposed;

    public float Width { get; }
    public float Height { get; }

    internal int PageObjectNumber { get; set; }
    internal int ContentObjectNumber { get; set; }
    internal bool UsesStandardFallbackFont { get; set; }

    public PdfPage(float width = 612f, float height = 792f) // Default US Letter (points)
    {
        Width = width;
        Height = height;
    }

    internal void AppendCommand(string cmd)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Use Latin1 (1:1 single-byte mapping 0x00..0xFF) instead of ASCII so bytes >= 0x80 are never mangled into '?' (0x3F).
        Pdf17Writer.WriteLatin1(_contentWriter, cmd);
        _contentWriter.WriteByte((byte)'\n');
    }

    internal void AppendBytes(ReadOnlySpan<byte> rawBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _contentWriter.Write(rawBytes);
    }

    internal string RegisterExtGState(float alpha, PdfBlendMode mode)
    {
        string key = $"GS_{alpha.ToString("F2", CultureInfo.InvariantCulture)}_{mode}";
        if (!_extGStates.ContainsKey(key))
        {
            _extGStates[key] = (alpha, mode);
        }
        return key;
    }

    internal IReadOnlyDictionary<string, (float Alpha, PdfBlendMode Mode)> ExtGStates => _extGStates;

    internal byte[] GetRawContent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _contentWriter.ToArray();
    }

    internal void CompressContentTo(PooledBufferWriter target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var zlib = new ZLibStream(new PooledBufferStream(target), CompressionLevel.Fastest, leaveOpen: true);
        _contentWriter.CopyTo(zlib);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _contentWriter.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// High-throughput PDF 1.7 document generator with Flate stream compression,
/// embedded raw TrueType fonts, ASCIIHex lossless inline RGB images, and ExtGState transparency.
/// Backed by pooled buffers (<see cref="PooledBufferWriter"/>).
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly List<PdfPage> _pages = new();
    private readonly List<TrueTypeFont> _embeddedFonts = new();
    private readonly List<long> _objectOffsets = new();
    private bool _disposed;

    public IReadOnlyList<PdfPage> Pages => _pages;

    public PdfPage AddPage(float width = 612f, float height = 792f)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var page = new PdfPage(width, height);
        _pages.Add(page);
        return page;
    }

    public void EmbedFont(TrueTypeFont font)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (font != null && !_embeddedFonts.Contains(font))
        {
            _embeddedFonts.Add(font);
        }
    }

    internal string ResolveFontResourceName(TrueTypeFont? font, PdfPage page)
    {
        if (font == null)
        {
            page.UsesStandardFallbackFont = true;
            return "F0";
        }

        int idx = _embeddedFonts.IndexOf(font);
        if (idx < 0)
        {
            _embeddedFonts.Add(font);
            idx = _embeddedFonts.Count - 1;
        }
        return $"F{idx + 1}";
    }

    public byte[] Save()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var docWriter = new PooledBufferWriter();
        WritePdf(docWriter);
        return docWriter.ToArray();
    }

    public void Save(Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(output);

        using var docWriter = new PooledBufferWriter();
        WritePdf(docWriter);
        docWriter.CopyTo(output);
    }

    private void WritePdf(PooledBufferWriter w)
    {
        _objectOffsets.Clear();
        _objectOffsets.Add(0); // Object 0 is free root entry

        Pdf17Writer.WriteHeader(w);

        int nextObj = 1;
        int catalogObj = nextObj++;
        int pagesObj = nextObj++;

        bool anyFallbackFont = false;
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].PageObjectNumber = nextObj++;
            _pages[i].ContentObjectNumber = nextObj++;
            if (_pages[i].UsesStandardFallbackFont)
            {
                anyFallbackFont = true;
            }
        }

        int fallbackHelveticaObj = anyFallbackFont ? nextObj++ : 0;

        var fontObjMap = new Dictionary<TrueTypeFont, (int FontObj, int FileObj)>(_embeddedFonts.Count);
        foreach (var font in _embeddedFonts)
        {
            fontObjMap[font] = (nextObj++, nextObj++);
        }

        WriteObjectHeader(w, catalogObj);
        Pdf17Writer.WriteAscii(w, "<< /Type /Catalog /Pages "u8);
        Pdf17Writer.WriteInt32(w, pagesObj);
        Pdf17Writer.WriteAscii(w, " 0 R >>\nendobj\n"u8);

        WriteObjectHeader(w, pagesObj);
        Pdf17Writer.WriteAscii(w, "<< /Type /Pages /Kids ["u8);
        foreach (var p in _pages)
        {
            Pdf17Writer.WriteInt32(w, p.PageObjectNumber);
            Pdf17Writer.WriteAscii(w, " 0 R "u8);
        }
        Pdf17Writer.WriteAscii(w, "] /Count "u8);
        Pdf17Writer.WriteInt32(w, _pages.Count);
        Pdf17Writer.WriteAscii(w, " >>\nendobj\n"u8);

        if (anyFallbackFont)
        {
            WriteObjectHeader(w, fallbackHelveticaObj);
            Pdf17Writer.WriteAscii(w, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n"u8);
        }

        using var compressedContent = new PooledBufferWriter();

        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];

            WriteObjectHeader(w, page.PageObjectNumber);
            Pdf17Writer.WriteAscii(w, "<< /Type /Page /Parent "u8);
            Pdf17Writer.WriteInt32(w, pagesObj);
            Pdf17Writer.WriteAscii(w, " 0 R\n/MediaBox [0 0 "u8);
            Pdf17Writer.WriteFloatCompact(w, page.Width);
            Pdf17Writer.WriteAscii(w, " "u8);
            Pdf17Writer.WriteFloatCompact(w, page.Height);
            Pdf17Writer.WriteAscii(w, "]\n/Contents "u8);
            Pdf17Writer.WriteInt32(w, page.ContentObjectNumber);
            Pdf17Writer.WriteAscii(w, " 0 R\n/Resources << /Font << "u8);

            if (page.UsesStandardFallbackFont)
            {
                Pdf17Writer.WriteAscii(w, "/F0 "u8);
                Pdf17Writer.WriteInt32(w, fallbackHelveticaObj);
                Pdf17Writer.WriteAscii(w, " 0 R "u8);
            }
            for (int f = 0; f < _embeddedFonts.Count; f++)
            {
                var info = fontObjMap[_embeddedFonts[f]];
                Pdf17Writer.WriteAscii(w, "/F"u8);
                Pdf17Writer.WriteInt32(w, f + 1);
                Pdf17Writer.WriteAscii(w, " "u8);
                Pdf17Writer.WriteInt32(w, info.FontObj);
                Pdf17Writer.WriteAscii(w, " 0 R "u8);
            }
            Pdf17Writer.WriteAscii(w, ">> "u8);

            if (page.ExtGStates.Count > 0)
            {
                Pdf17Writer.WriteAscii(w, "/ExtGState << "u8);
                foreach (var kvp in page.ExtGStates)
                {
                    Pdf17Writer.WriteAscii(w, "/");
                    Pdf17Writer.WriteAscii(w, kvp.Key);
                    Pdf17Writer.WriteAscii(w, " << /Type /ExtGState /ca "u8);
                    Pdf17Writer.WriteFloatCompact(w, kvp.Value.Alpha);
                    Pdf17Writer.WriteAscii(w, " /CA "u8);
                    Pdf17Writer.WriteFloatCompact(w, kvp.Value.Alpha);
                    Pdf17Writer.WriteAscii(w, " /BM /"u8);
                    Pdf17Writer.WriteAscii(w, kvp.Value.Mode.ToString());
                    Pdf17Writer.WriteAscii(w, " >> "u8);
                }
                Pdf17Writer.WriteAscii(w, ">> "u8);
            }

            Pdf17Writer.WriteAscii(w, ">> >>\nendobj\n"u8);

            compressedContent.Reset();
            page.CompressContentTo(compressedContent);

            WriteObjectHeader(w, page.ContentObjectNumber);
            Pdf17Writer.WriteAscii(w, "<< /Length "u8);
            Pdf17Writer.WriteInt32(w, compressedContent.WrittenCount);
            Pdf17Writer.WriteAscii(w, " /Filter /FlateDecode >>\nstream\n"u8);
            compressedContent.CopyTo(w);
            Pdf17Writer.WriteAscii(w, "\nendstream\nendobj\n"u8);
        }

        foreach (var kvp in fontObjMap)
        {
            var font = kvp.Key;
            var (fontObj, fileObj) = kvp.Value;

            ReadOnlyMemory<byte> fontBytes = font.FontData;

            WriteObjectHeader(w, fileObj);
            Pdf17Writer.WriteAscii(w, "<< /Length "u8);
            Pdf17Writer.WriteInt32(w, fontBytes.Length);
            Pdf17Writer.WriteAscii(w, " /Length1 "u8);
            Pdf17Writer.WriteInt32(w, fontBytes.Length);
            Pdf17Writer.WriteAscii(w, " >>\nstream\n"u8);
            w.Write(fontBytes.Span);
            Pdf17Writer.WriteAscii(w, "\nendstream\nendobj\n"u8);

            WriteObjectHeader(w, fontObj);
            Pdf17Writer.WriteAscii(w, "<< /Type /Font /Subtype /TrueType\n/BaseFont /GlacierFont\n/FontFile2 "u8);
            Pdf17Writer.WriteInt32(w, fileObj);
            Pdf17Writer.WriteAscii(w, " 0 R\n/FirstChar 32 /LastChar 255\n/Widths ["u8);
            for (int c = 32; c <= 255; c++)
            {
                Pdf17Writer.WriteInt32(w, font.GetPdfWidth(c));
                w.WriteByte((byte)' ');
            }
            Pdf17Writer.WriteAscii(w, "]\n>>\nendobj\n"u8);
        }

        long xrefStart = w.WrittenLength;
        Pdf17Writer.WriteXrefAndTrailer(w, CollectionsMarshal.AsSpan(_objectOffsets), _objectOffsets.Count, catalogObj, xrefStart);
    }

    private void WriteObjectHeader(PooledBufferWriter w, int objectNumber)
    {
        while (_objectOffsets.Count <= objectNumber)
        {
            _objectOffsets.Add(0);
        }
        _objectOffsets[objectNumber] = w.WrittenLength;
        Pdf17Writer.WriteInt32(w, objectNumber);
        Pdf17Writer.WriteAscii(w, " 0 obj\n"u8);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var page in _pages)
            {
                page.Dispose();
            }
            _pages.Clear();
            _embeddedFonts.Clear();
            _objectOffsets.Clear();
            _disposed = true;
        }
    }
}
