namespace Glacier.Graphics.Tests;

using System;
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

        string pdfText = Encoding.ASCII.GetString(pdfBytes);
        Assert.StartsWith("%PDF-1.7", pdfText);
        Assert.Contains("/Root", pdfText);
        Assert.Contains("/Pages", pdfText);
        Assert.Contains("/Filter /FlateDecode", pdfText);
        Assert.Contains("xref", pdfText);
        Assert.Contains("trailer", pdfText);
        Assert.Contains("%%EOF", pdfText);
    }
}
