namespace Glacier.Graphics.Pdf;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
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
public sealed class PdfPage
{
    private readonly MemoryStream _contentStream = new();
    private readonly Dictionary<string, (float Alpha, PdfBlendMode Mode)> _extGStates = new();

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
        // Use Latin1 (1:1 single-byte mapping 0x00..0xFF) instead of ASCII so bytes >= 0x80 are never mangled into '?' (0x3F).
        byte[] bytes = Encoding.Latin1.GetBytes(cmd);
        _contentStream.Write(bytes, 0, bytes.Length);
        _contentStream.WriteByte((byte)'\n');
    }

    internal void AppendBytes(ReadOnlySpan<byte> rawBytes)
    {
        _contentStream.Write(rawBytes);
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

    internal byte[] GetRawContent() => _contentStream.ToArray();

    internal byte[] GetCompressedContent()
    {
        byte[] raw = _contentStream.ToArray();
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }
}

/// <summary>
/// High-throughput PDF 1.7 / 2.0 document generator with Flate stream compression,
/// vector paths, embedded TrueType fonts, lossless RGB image embedding, and ExtGState transparency.
/// </summary>
public sealed class PdfDocument
{
    private readonly List<PdfPage> _pages = new();
    private readonly List<TrueTypeFont> _embeddedFonts = new();
    private readonly List<long> _objectOffsets = new();

    public IReadOnlyList<PdfPage> Pages => _pages;

    public PdfPage AddPage(float width = 612f, float height = 792f)
    {
        var page = new PdfPage(width, height);
        _pages.Add(page);
        return page;
    }

    public void EmbedFont(TrueTypeFont font)
    {
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
        using var ms = new MemoryStream();
        Save(ms);
        return ms.ToArray();
    }

    public void Save(Stream output)
    {
        // Use Latin1 so the 4 high-bit PDF binary header bytes (%\xE2\xE3\xCF\xD3) are preserved verbatim.
        using var writer = new StreamWriter(output, Encoding.Latin1, leaveOpen: true);
        _objectOffsets.Clear();
        _objectOffsets.Add(0); // Object 0 is free root entry

        writer.Write("%PDF-1.7\n");
        writer.Write("%\u00E2\u00E3\u00CF\u00D3\n");
        writer.Flush();

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

        var fontObjMap = new Dictionary<TrueTypeFont, (int FontObj, int FileObj)>();
        foreach (var font in _embeddedFonts)
        {
            fontObjMap[font] = (nextObj++, nextObj++);
        }

        WriteObjectHeader(writer, output, catalogObj);
        writer.Write($"<< /Type /Catalog /Pages {pagesObj} 0 R >>\nendobj\n");
        writer.Flush();

        WriteObjectHeader(writer, output, pagesObj);
        writer.Write("<< /Type /Pages /Kids [");
        foreach (var p in _pages)
        {
            writer.Write($"{p.PageObjectNumber} 0 R ");
        }
        writer.Write($"] /Count {_pages.Count} >>\nendobj\n");
        writer.Flush();

        if (anyFallbackFont)
        {
            WriteObjectHeader(writer, output, fallbackHelveticaObj);
            writer.Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
            writer.Flush();
        }

        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];

            WriteObjectHeader(writer, output, page.PageObjectNumber);
            writer.Write($"<< /Type /Page /Parent {pagesObj} 0 R\n");
            writer.Write(string.Format(CultureInfo.InvariantCulture, "/MediaBox [0 0 {0:0.##} {1:0.##}]\n", page.Width, page.Height));
            writer.Write($"/Contents {page.ContentObjectNumber} 0 R\n");

            writer.Write("/Resources << /Font << ");
            if (page.UsesStandardFallbackFont)
            {
                writer.Write($"/F0 {fallbackHelveticaObj} 0 R ");
            }
            for (int f = 0; f < _embeddedFonts.Count; f++)
            {
                var info = fontObjMap[_embeddedFonts[f]];
                writer.Write($"/F{f + 1} {info.FontObj} 0 R ");
            }
            writer.Write(">> ");

            if (page.ExtGStates.Count > 0)
            {
                writer.Write("/ExtGState << ");
                foreach (var kvp in page.ExtGStates)
                {
                    writer.Write(string.Format(
                        CultureInfo.InvariantCulture,
                        "/{0} << /Type /ExtGState /ca {1:0.##} /CA {1:0.##} /BM /{2} >> ",
                        kvp.Key, kvp.Value.Alpha, kvp.Value.Mode));
                }
                writer.Write(">> ");
            }

            writer.Write(">> >>\nendobj\n");
            writer.Flush();

            byte[] compressed = page.GetCompressedContent();
            WriteObjectHeader(writer, output, page.ContentObjectNumber);
            writer.Write($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            writer.Flush();
            output.Write(compressed, 0, compressed.Length);
            writer.Write("\nendstream\nendobj\n");
            writer.Flush();
        }

        foreach (var kvp in fontObjMap)
        {
            var font = kvp.Key;
            var (fontObj, fileObj) = kvp.Value;

            ReadOnlyMemory<byte> fontBytes = font.FontData;

            WriteObjectHeader(writer, output, fileObj);
            writer.Write($"<< /Length {fontBytes.Length} /Length1 {fontBytes.Length} >>\nstream\n");
            writer.Flush();
            output.Write(fontBytes.Span);
            writer.Write("\nendstream\nendobj\n");
            writer.Flush();

            WriteObjectHeader(writer, output, fontObj);
            writer.Write("<< /Type /Font /Subtype /TrueType\n");
            writer.Write("/BaseFont /GlacierFont\n");
            writer.Write($"/FontFile2 {fileObj} 0 R\n");
            writer.Write("/FirstChar 32 /LastChar 255\n");
            writer.Write("/Widths [");
            for (int c = 32; c <= 255; c++)
            {
                int gid = font.GetGlyphIndex(c);
                ushort adv = font.GetAdvanceWidth(gid);
                int pdfWidth = (int)Math.Round((adv * 1000.0) / font.UnitsPerEm);
                writer.Write($"{pdfWidth} ");
            }
            writer.Write("]\n");
            writer.Write(">>\nendobj\n");
            writer.Flush();
        }

        long xrefStart = output.Position;
        writer.Write($"xref\n0 {_objectOffsets.Count}\n");
        writer.Write("0000000000 65535 f \n");
        for (int i = 1; i < _objectOffsets.Count; i++)
        {
            writer.Write(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", _objectOffsets[i]));
        }

        writer.Write("trailer\n");
        writer.Write($"<< /Size {_objectOffsets.Count} /Root {catalogObj} 0 R >>\n");
        writer.Write($"startxref\n{xrefStart}\n%%EOF\n");
        writer.Flush();
    }

    private void WriteObjectHeader(StreamWriter writer, Stream output, int objectNumber)
    {
        writer.Flush();
        while (_objectOffsets.Count <= objectNumber)
        {
            _objectOffsets.Add(0);
        }
        _objectOffsets[objectNumber] = output.Position;
        writer.Write($"{objectNumber} 0 obj\n");
    }
}
