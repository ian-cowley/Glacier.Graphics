namespace Glacier.Graphics.Pdf;

using System;
using System.Globalization;
using System.Text;
using Glacier.Graphics.Text;
using Glacier.Graphics.Vector;

/// <summary>
/// Vector PDF graphics canvas implementing <see cref="IGraphicsCanvas"/>.
/// Emits resolution-independent vector PDF drawing operators directly to a <see cref="PdfPage"/>.
/// </summary>
public sealed class PdfGraphicsCanvas : IGraphicsCanvas
{
    private readonly PdfDocument _document;
    private readonly PdfPage _page;
    private bool _disposed;

    public int Width => (int)MathF.Round(_page.Width);
    public int Height => (int)MathF.Round(_page.Height);

    public PdfGraphicsCanvas(PdfDocument document, PdfPage page)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _page = page ?? throw new ArgumentNullException(nameof(page));
    }

    public void Clear(Rgba32 color)
    {
        if (color.A == 0) return;
        var rectPath = new VectorPath();
        rectPath.AddRect(0, 0, Width, Height);
        FillPath(rectPath, new Paint(color));
    }

    public void DrawPath(in VectorPath path, in Paint paint)
    {
        if (path == null || path.VerbCount == 0 || paint.Color.A == 0) return;

        SetAlpha(paint.Color.A / 255.0f);
        SetStrokeColor(paint.Color);
        _page.AppendCommand(string.Format(CultureInfo.InvariantCulture, "{0:0.##} w", paint.StrokeWidth));

        int capVal = paint.Cap switch
        {
            StrokeCap.Butt => 0,
            StrokeCap.Round => 1,
            StrokeCap.Square => 2,
            _ => 0
        };
        _page.AppendCommand($"{capVal} J");

        int joinVal = paint.Join switch
        {
            StrokeJoin.Miter => 0,
            StrokeJoin.Round => 1,
            StrokeJoin.Bevel => 2,
            _ => 0
        };
        _page.AppendCommand($"{joinVal} j");
        _page.AppendCommand(string.Format(CultureInfo.InvariantCulture, "{0:0.##} M", paint.MiterLimit));

        EmitPathOperators(path);
        _page.AppendCommand("S");
    }

    public void FillPath(in VectorPath path, in Paint paint)
    {
        if (path == null || path.VerbCount == 0 || paint.Color.A == 0) return;

        SetAlpha(paint.Color.A / 255.0f);
        SetFillColor(paint.Color);

        EmitPathOperators(path);
        _page.AppendCommand(path.FillRule == WindingRule.EvenOdd ? "f*" : "f");
    }

    public void DrawText(ReadOnlySpan<char> text, float x, float y, in Font font, in Paint paint)
    {
        if (text.IsEmpty || paint.Color.A == 0) return;

        string fontResName = _document.ResolveFontResourceName(font.Typeface, _page);

        SetAlpha(paint.Color.A / 255.0f);
        SetFillColor(paint.Color);

        float pdfY = _page.Height - y;

        _page.AppendCommand("BT");
        _page.AppendCommand(string.Format(CultureInfo.InvariantCulture, "/{0} {1:0.##} Tf", fontResName, font.Size));
        _page.AppendCommand(string.Format(CultureInfo.InvariantCulture, "1 0 0 1 {0:0.##} {1:0.##} Tm", x, pdfY));

        var sb = new StringBuilder("(");
        foreach (char c in text)
        {
            if (c == '(' || c == ')' || c == '\\') sb.Append('\\');
            sb.Append(c <= 0xFF ? c : '?');
        }
        sb.Append(") Tj");

        _page.AppendCommand(sb.ToString());
        _page.AppendCommand("ET");
    }

    public void DrawImage(in ReadOnlySpan2D<Rgba32> image, float x, float y, float width, float height)
    {
        if (image.Width <= 0 || image.Height <= 0 || width <= 0f || height <= 0f) return;

        float pdfY = _page.Height - y - height;
        _page.AppendCommand("q");
        _page.AppendCommand(string.Format(CultureInfo.InvariantCulture, "{0:0.##} 0 0 {1:0.##} {2:0.##} {3:0.##} cm", width, height, x, pdfY));
        _page.AppendCommand("BI");
        _page.AppendCommand($"/W {image.Width} /H {image.Height} /CS /RGB /BPC 8 /F /AHx");
        _page.AppendCommand("ID");

        // Emit lossless ASCIIHexDecode RGB stream (immune to >=128 ASCII truncation and inline 'EI' token collisions)
        ReadOnlySpan<byte> hexLut = "0123456789ABCDEF"u8;
        byte[] hexRow = new byte[image.Width * 6 + 1];
        for (int row = 0; row < image.Height; row++)
        {
            var rSpan = image.GetRowSpan(row);
            int pos = 0;
            for (int col = 0; col < image.Width; col++)
            {
                Rgba32 px = rSpan[col];
                hexRow[pos++] = hexLut[px.R >> 4];
                hexRow[pos++] = hexLut[px.R & 0x0F];
                hexRow[pos++] = hexLut[px.G >> 4];
                hexRow[pos++] = hexLut[px.G & 0x0F];
                hexRow[pos++] = hexLut[px.B >> 4];
                hexRow[pos++] = hexLut[px.B & 0x0F];
            }
            hexRow[pos++] = (byte)'\n';
            _page.AppendBytes(hexRow.AsSpan(0, pos));
        }

        _page.AppendCommand(">");
        _page.AppendCommand("EI");
        _page.AppendCommand("Q");
    }

    public void Save() => _page.AppendCommand("q");

    public void Restore() => _page.AppendCommand("Q");

    public void ClipPath(in VectorPath path)
    {
        if (path == null || path.VerbCount == 0) return;
        EmitPathOperators(path);
        _page.AppendCommand(path.FillRule == WindingRule.EvenOdd ? "W* n" : "W n");
    }

    public void Flush() { }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    private void SetStrokeColor(Rgba32 c)
    {
        _page.AppendCommand(string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.###} {1:0.###} {2:0.###} RG",
            c.R / 255.0f, c.G / 255.0f, c.B / 255.0f));
    }

    private void SetFillColor(Rgba32 c)
    {
        _page.AppendCommand(string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.###} {1:0.###} {2:0.###} rg",
            c.R / 255.0f, c.G / 255.0f, c.B / 255.0f));
    }

    private void SetAlpha(float alpha)
    {
        if (alpha < 0.999f)
        {
            string gsKey = _page.RegisterExtGState(alpha, PdfBlendMode.Normal);
            _page.AppendCommand($"/{gsKey} gs");
        }
    }

    private void EmitPathOperators(VectorPath path)
    {
        int ptIdx = 0;
        float h = _page.Height;

        for (int v = 0; v < path.VerbCount; v++)
        {
            switch (path.Verbs[v])
            {
                case PathVerb.MoveTo:
                {
                    PointF p = path.Points[ptIdx++];
                    _page.AppendCommand(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} m", p.X, h - p.Y));
                    break;
                }
                case PathVerb.LineTo:
                {
                    PointF p = path.Points[ptIdx++];
                    _page.AppendCommand(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} l", p.X, h - p.Y));
                    break;
                }
                case PathVerb.QuadTo:
                {
                    PointF p1 = path.Points[ptIdx++];
                    PointF p2 = path.Points[ptIdx++];
                    PointF p0 = path.Points[ptIdx - 3];
                    PointF c1 = p0 + (p1 - p0) * (2f / 3f);
                    PointF c2 = p2 + (p1 - p2) * (2f / 3f);
                    _page.AppendCommand(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.##} {1:0.##} {2:0.##} {3:0.##} {4:0.##} {5:0.##} c",
                        c1.X, h - c1.Y, c2.X, h - c2.Y, p2.X, h - p2.Y));
                    break;
                }
                case PathVerb.CubicTo:
                {
                    PointF p1 = path.Points[ptIdx++];
                    PointF p2 = path.Points[ptIdx++];
                    PointF p3 = path.Points[ptIdx++];
                    _page.AppendCommand(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.##} {1:0.##} {2:0.##} {3:0.##} {4:0.##} {5:0.##} c",
                        p1.X, h - p1.Y, p2.X, h - p2.Y, p3.X, h - p3.Y));
                    break;
                }
                case PathVerb.Close:
                    _page.AppendCommand("h");
                    break;
            }
        }
    }
}
