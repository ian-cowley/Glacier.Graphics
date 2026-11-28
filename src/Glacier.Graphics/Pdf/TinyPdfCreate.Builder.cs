using System.Buffers;
using System.IO.Compression;

namespace Glacier.Graphics.Pdf;

public partial class TinyPdfCreate
{
    /// <summary>
    /// A builder for constructing PDF 1.7 documents page by page backed by <see cref="PooledBufferWriter"/>.
    /// </summary>
    public partial class Builder
    {
        /// <summary>
        /// Gets or sets whether to compress the generated PDF objects. Defaults to true.
        /// </summary>
        public bool Compress { get; set; } = true;
        private readonly List<PdfObject> _objects = new List<PdfObject>();
        private readonly List<Ref> _pages = new List<Ref>();
        private int _nextId = 1;

        public Ref AddObject(Dictionary<string, object> dict, byte[]? streamBytes = null)
        {
            var id = _nextId++;
            var obj = new PdfObject { Id = id, Dict = dict, Stream = streamBytes };
            _objects.Add(obj);
            return new Ref(id);
        }

        /// <summary>
        /// Adds a new page to the PDF with the specified dimensions.
        /// </summary>
        /// <param name="width">The width of the page in points.</param>
        /// <param name="height">The height of the page in points.</param>
        /// <param name="fn">An action that takes an <see cref="IPageContext"/> to draw on the page.</param>
        public void Page(double width, double height, Action<IPageContext> fn)
        {
            using var writer = new Glacier.Graphics.Pdf.PooledBufferWriter();
            var images = new List<(string Name, Ref Ref)>();
            var links = new List<(string Url, double[] Rect)>();
            int imageCount = 0;

            var ctx = new PageContextImpl(this, writer, images, imageCount, (newCount) => imageCount = newCount, links);
            fn(ctx);

            byte[] contentBytes = writer.ToArray();
            var contentRef = AddObject(new Dictionary<string, object> { ["Length"] = contentBytes.Length }, contentBytes);

            var xobjects = new Dictionary<string, object>();
            foreach (var img in images) xobjects[img.Name[1..]] = img.Ref;

            var annots = new List<Ref>();
            foreach (var lnk in links)
            {
                annots.Add(AddObject(new Dictionary<string, object>
                {
                    ["Type"] = "/Annot",
                    ["Subtype"] = "/Link",
                    ["Rect"] = new List<double> { lnk.Rect[0], lnk.Rect[1], lnk.Rect[2], lnk.Rect[3] },
                    ["Border"] = new List<int> { 0, 0, 0 },
                    ["A"] = new Dictionary<string, object>
                    {
                        ["Type"] = "/Action",
                        ["S"] = "/URI",
                        ["URI"] = lnk.Url
                    }
                }));
            }

            var pageDict = new Dictionary<string, object>
            {
                ["Type"] = "/Page",
                ["Parent"] = null!, // Will be set in Build
                ["MediaBox"] = new List<double> { 0, 0, width, height },
                ["Contents"] = contentRef,
                ["Resources"] = new Dictionary<string, object>
                {
                    ["Font"] = new Dictionary<string, object> { ["F1"] = null!, ["F2"] = null!, ["F3"] = null! },
                    ["XObject"] = xobjects.Count > 0 ? xobjects : null!
                }
            };
            if (annots.Count > 0) pageDict["Annots"] = annots;

            var pageRef = AddObject(pageDict);
            _pages.Add(pageRef);
        }

        /// <summary>
        /// Adds a new page to the PDF with default dimensions (Letter: 612x792).
        /// </summary>
        /// <param name="fn">An action that takes an <see cref="IPageContext"/> to draw on the page.</param>
        public void Page(Action<IPageContext> fn) => Page(612, 792, fn);

        /// <summary>
        /// Measures the width of a text string in the default font (Helvetica) and specified size.
        /// </summary>
        /// <param name="str">The text to measure.</param>
        /// <param name="size">The font size.</param>
        /// <returns>The width of the text in PDF points.</returns>
        public double MeasureText(string str, double size) => TinyPdfCreate.MeasureText(str, size);

        /// <summary>
        /// Finalizes the PDF 1.7 construction and returns the raw bytes of the PDF document.
        /// </summary>
        /// <returns>The raw bytes of the generated PDF document.</returns>
        public byte[] Build()
        {
            int transientId = _nextId;
            var transientObjects = new List<PdfObject>(16);

            Ref AddTransient(Dictionary<string, object> dict)
            {
                int id = transientId++;
                transientObjects.Add(new PdfObject { Id = id, Dict = dict });
                return new Ref(id);
            }

            var fontMap = new Dictionary<PdfFont, Ref>(14)
            {
                [PdfFont.Helvetica] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Helvetica", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.Times] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Times-Roman", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.Courier] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Courier", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.HelveticaBold] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Helvetica-Bold", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.HelveticaOblique] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Helvetica-Oblique", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.HelveticaBoldOblique] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Helvetica-BoldOblique", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.TimesBold] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Times-Bold", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.TimesItalic] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Times-Italic", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.TimesBoldItalic] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Times-BoldItalic", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.CourierBold] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Courier-Bold", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.CourierOblique] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Courier-Oblique", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.CourierBoldOblique] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Courier-BoldOblique", ["Encoding"] = "/WinAnsiEncoding" }),
                [PdfFont.Symbol] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/Symbol" }),
                [PdfFont.ZapfDingbats] = AddTransient(new Dictionary<string, object> { ["Type"] = "/Font", ["Subtype"] = "/Type1", ["BaseFont"] = "/ZapfDingbats" })
            };

            var pagesRef = AddTransient(new Dictionary<string, object> { ["Type"] = "/Pages", ["Kids"] = _pages, ["Count"] = _pages.Count });

            foreach (var obj in _objects)
            {
                if (obj.Dict.TryGetValue("Type", out var type) && type?.ToString() == "/Page")
                {
                    obj.Dict["Parent"] = pagesRef;
                    if (obj.Dict.TryGetValue("Resources", out var resObj) && resObj is Dictionary<string, object> resources)
                    {
                        if (resources.TryGetValue("Font", out var fontObj) && fontObj is Dictionary<string, object> fonts)
                        {
                            foreach (var kvp in fontMap)
                            {
                                string tag = GetFontTag(kvp.Key);
                                fonts[tag.TrimStart('/')] = kvp.Value;
                            }
                        }
                    }
                }
            }

            var catalogRef = AddTransient(new Dictionary<string, object> { ["Type"] = "/Catalog", ["Pages"] = pagesRef });

            int totalObjects = transientId - 1;
            var offsets = new long[totalObjects + 1];

            using var docWriter = new Glacier.Graphics.Pdf.PooledBufferWriter();
            using var compWriter = new Glacier.Graphics.Pdf.PooledBufferWriter();

            Pdf17Writer.WriteHeader(docWriter);

            void EmitObject(PdfObject obj)
            {
                offsets[obj.Id] = docWriter.WrittenLength;
                Pdf17Writer.WriteInt32(docWriter, obj.Id);
                Pdf17Writer.WriteAscii(docWriter, " 0 obj\n"u8);

                if (obj.Stream != null)
                {
                    byte[] streamData = obj.Stream;
                    // Never overwrite pre-encoded streams (e.g., JPEG /DCTDecode XObjects) with /FlateDecode!
                    bool hasPreEncodedFilter = obj.Dict.TryGetValue("Filter", out var existingFilter)
                        && existingFilter?.ToString() != "/FlateDecode";
                    bool shouldCompress = Compress && !hasPreEncodedFilter;

                    if (shouldCompress)
                    {
                        compWriter.Reset();
                        using (var zs = new ZLibStream(new PooledBufferStream(compWriter), CompressionLevel.Optimal, leaveOpen: true))
                        {
                            zs.Write(streamData, 0, streamData.Length);
                        }
                        obj.Dict["Filter"] = "/FlateDecode";
                        obj.Dict["Length"] = compWriter.WrittenCount;
                    }
                    else
                    {
                        if (!hasPreEncodedFilter)
                        {
                            obj.Dict.Remove("Filter");
                        }
                        obj.Dict["Length"] = streamData.Length;
                    }

                    SerializeToWriter(docWriter, obj.Dict);
                    Pdf17Writer.WriteAscii(docWriter, "\nstream\n"u8);

                    if (shouldCompress)
                    {
                        compWriter.CopyTo(docWriter);
                    }
                    else
                    {
                        docWriter.Write(streamData);
                    }

                    Pdf17Writer.WriteAscii(docWriter, "\nendstream\nendobj\n"u8);
                }
                else
                {
                    SerializeToWriter(docWriter, obj.Dict);
                    Pdf17Writer.WriteAscii(docWriter, "\nendobj\n"u8);
                }
            }

            foreach (var obj in _objects) EmitObject(obj);
            foreach (var obj in transientObjects) EmitObject(obj);

            long xrefOffset = docWriter.WrittenLength;
            Pdf17Writer.WriteXrefAndTrailer(docWriter, offsets, totalObjects + 1, catalogRef.Id, xrefOffset);

            return docWriter.ToArray();
        }
    }
}
