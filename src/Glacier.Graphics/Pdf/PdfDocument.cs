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
    private readonly List<string> _fontsUsed = new();
    private readonly Dictionary<string, (float Alpha, PdfBlendMode Mode)> _extGStates = new();

    public float Width { get; }
    public float Height { get; }

    internal int PageObjectNumber { get; set; }
    internal int ContentObjectNumber { get; set; }

    public PdfPage(float width = 612f, float height = 792f) // Default US Letter (points)
    {
        Width = width;
        Height = height;
    }

    internal void AppendCommand(string cmd)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(cmd + "\n");
        _contentStream.Write(bytes, 0, bytes.Length);
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
/// vector paths, embedded TrueType fonts, and ExtGState transparency.
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

    public byte[] Save()
    {
        using var ms = new MemoryStream();
        Save(ms);
        return ms.ToArray();
    }

    public void Save(Stream output)
    {
        using var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true);
        _objectOffsets.Clear();
        _objectOffsets.Add(0); // Object 0 is dummy

        // 1. PDF Header (PDF 1.7 standard)
        writer.Write("%PDF-1.7\n");
        writer.Write("%\xE2\xE3\xCF\xD3\n"); // Binary marker
        writer.Flush();

        int nextObj = 1;

        // Reserve object numbers
        int catalogObj = nextObj++;
        int pagesObj = nextObj++;

        // Assign object numbers to pages and content streams
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].PageObjectNumber = nextObj++;
            _pages[i].ContentObjectNumber = nextObj++;
        }

        // Embedded fonts object allocation
        var fontObjMap = new Dictionary<TrueTypeFont, (int FontObj, int FileObj)>();
        foreach (var font in _embeddedFonts)
        {
            fontObjMap[font] = (nextObj++, nextObj++);
        }

        // Write Catalog
        WriteObjectHeader(writer, output, catalogObj);
        writer.Write($"<< /Type /Catalog /Pages {pagesObj} 0 R >>\nendobj\n");
        writer.Flush();

        // Write Pages Tree
        WriteObjectHeader(writer, output, pagesObj);
        writer.Write("<< /Type /Pages /Kids [");
        foreach (var p in _pages)
        {
            writer.Write($"{p.PageObjectNumber} 0 R ");
        }
        writer.Write($"] /Count {_pages.Count} >>\nendobj\n");
        writer.Flush();

        // Write Pages & Contents
        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];

            // Write Page Object
            WriteObjectHeader(writer, output, page.PageObjectNumber);
            writer.Write($"<< /Type /Page /Parent {pagesObj} 0 R\n");
            writer.Write(string.Format(CultureInfo.InvariantCulture, "/MediaBox [0 0 {0:0.##} {1:0.##}]\n", page.Width, page.Height));
            writer.Write($"/Contents {page.ContentObjectNumber} 0 R\n");

            // Resources
            writer.Write("/Resources << /Font << ");
            int fontIdx = 1;
            foreach (var kvp in fontObjMap)
            {
                writer.Write($"/F{fontIdx++} {kvp.Value.FontObj} 0 R ");
            }
            writer.Write(">> ");

            // ExtGState transparency resources
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

            // Write Content Stream Object
            byte[] compressed = page.GetCompressedContent();
            WriteObjectHeader(writer, output, page.ContentObjectNumber);
            writer.Write($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            writer.Flush();
            output.Write(compressed, 0, compressed.Length);
            writer.Write("\nendstream\nendobj\n");
            writer.Flush();
        }

        // Write Embedded Fonts
        foreach (var kvp in fontObjMap)
        {
            var font = kvp.Key;
            var (fontObj, fileObj) = kvp.Value;

            ReadOnlyMemory<byte> fontBytes = font.FontData;

            // Write FontFile2 stream
            WriteObjectHeader(writer, output, fileObj);
            writer.Write($"<< /Length {fontBytes.Length} /Length1 {fontBytes.Length} >>\nstream\n");
            writer.Flush();
            output.Write(fontBytes.Span);
            writer.Write("\nendstream\nendobj\n");
            writer.Flush();

            // Write TrueType Font Object
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

        // Write Cross-Reference Table (xref)
        long xrefStart = output.Position;
        writer.Write($"xref\n0 {_objectOffsets.Count}\n");
        writer.Write("0000000000 65535 f \n");
        for (int i = 1; i < _objectOffsets.Count; i++)
        {
            writer.Write(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", _objectOffsets[i]));
        }

        // Write Trailer
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
