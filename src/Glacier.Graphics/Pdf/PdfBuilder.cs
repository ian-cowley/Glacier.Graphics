namespace Glacier.Graphics.Pdf;

/// <summary>
/// Alias and factory for TinyPdfCreate providing fluent PDF construction within Glacier.Graphics.Pdf.
/// </summary>
public static class PdfBuilder
{
    public static TinyPdfCreate.Builder Create(bool compress = true) => new() { Compress = compress };
    public static byte[] FromMarkdown(string markdown, TinyPdfCreate.MarkdownOptions? options = null) => TinyPdfCreate.Markdown(markdown, options);
}
