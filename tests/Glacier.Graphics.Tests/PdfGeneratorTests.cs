namespace Glacier.Graphics.Tests;

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Glacier.Graphics;
using Glacier.Graphics.Pdf;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class PdfGeneratorTests
{
    [Fact]
    public void PdfDocument_GeneratesValidPdfBytes()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(width: 500, height: 700);

        using (var canvas = new PdfGraphicsCanvas(doc, page))
        {
            canvas.Clear(Rgba32.White);

            var path = new VectorPath();
            path.AddRect(50, 50, 200, 100);
            canvas.FillPath(path, new Paint(Rgba32.Blue));

            var strokePath = new VectorPath();
            strokePath.AddLine(10, 10, 300, 300);
            canvas.DrawPath(strokePath, new Paint(Rgba32.Red, PaintStyle.Stroke, StrokeWidth: 3f));

            var font = new Font(size: 14f);
            canvas.DrawText("Glacier Vector PDF", 50, 400, font, new Paint(Rgba32.Black));
        }

        byte[] pdfBytes = doc.Save();
        Assert.True(pdfBytes.Length > 0);

        string pdfText = Encoding.Latin1.GetString(pdfBytes);
        Assert.StartsWith("%PDF-1.7", pdfText);
        Assert.Contains("/Root", pdfText);
        Assert.Contains("/Pages", pdfText);
        Assert.Contains("/Filter /FlateDecode", pdfText);
        Assert.Contains("xref", pdfText);
        Assert.Contains("trailer", pdfText);
        Assert.Contains("%%EOF", pdfText);
    }

    [Fact]
    public void PdfDocument_PreservesBinaryHeaderFallbackFontAndHighByteImages()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage(width: 200, height: 200);

        Rgba32[] pixels =
        [
            new Rgba32(250, 200, 128, 255), // 0xFA, 0xC8, 0x80 (all >= 128!)
            new Rgba32(10, 240, 180, 255)   // 0x0A, 0xF0, 0xB4
        ];

        using (var canvas = new PdfGraphicsCanvas(doc, page))
        {
            var imgSpan = new ReadOnlySpan2D<Rgba32>(pixels, 2, 1, 2);
            canvas.DrawImage(imgSpan, 10, 20, 40, 20);
            canvas.DrawText("Fallback Font", 10, 80, new Font(size: 12f), new Paint(Rgba32.Black));
        }

        byte[] pdfBytes = doc.Save();
        string latin1Pdf = Encoding.Latin1.GetString(pdfBytes);

        // PDF 1.7 binary marker comment (% + 0xE2 0xE3 0xCF 0xD3) must NOT be corrupted to '?' (0x3F)
        int secondLineIdx = Array.IndexOf(pdfBytes, (byte)'\n') + 1;
        Assert.Equal((byte)'%', pdfBytes[secondLineIdx]);
        Assert.Equal(0xE2, pdfBytes[secondLineIdx + 1]);
        Assert.Equal(0xE3, pdfBytes[secondLineIdx + 2]);
        Assert.Equal(0xCF, pdfBytes[secondLineIdx + 3]);
        Assert.Equal(0xD3, pdfBytes[secondLineIdx + 4]);

        // Fallback Type1 /Helvetica font resource must be registered when Font.Typeface is null
        Assert.Contains("/Subtype /Type1 /BaseFont /Helvetica", latin1Pdf);
        Assert.Contains("/Font << /F0 ", latin1Pdf);

        // Note: page 1 content stream is the first stream in the document (before any embedded TrueType FontFile2 streams)
        const string streamMarker = "/Filter /FlateDecode >>\nstream\n";
        const string endStreamMarker = "\nendstream";
        int streamStart = latin1Pdf.IndexOf(streamMarker, StringComparison.Ordinal) + streamMarker.Length;
        int streamEnd = latin1Pdf.IndexOf(endStreamMarker, streamStart, StringComparison.Ordinal);
        using var compressed = new MemoryStream(pdfBytes, streamStart, streamEnd - streamStart);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);

        string rawCommands = Encoding.Latin1.GetString(decompressed.ToArray());
        Assert.Contains("/F /AHx", rawCommands);
        Assert.Contains("FAC8800AF0B4\n>", rawCommands);
    }
}
