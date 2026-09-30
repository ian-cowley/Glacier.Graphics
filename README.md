# 🎨 Glacier.Graphics

**Pure C# .NET 10 Hardware-Accelerated 2D Vector Graphics, Compute Rasterizer, Font Engine, SIMD Codecs, and Vector PDF Generator.**

Part of the **Glacier High-Performance Ecosystem**. 100% pure managed C# code with zero native dependencies (eliminating SkiaSharp, native HarfBuzz, FreeType, libpng, and libjpeg).

---

## Features

- **Glacier.Graphics.Vector**:
  - SIMD Bézier curve evaluation (`Vector512<float>`, `Vector256<float>`, and scalar fallback).
  - Analytical line stroker supporting Miter, Round, and Bevel joins, Butt, Square, and Round caps, and non-allocating dashing iterator.
  - NonZero and EvenOdd winding rules.
  - Zero-allocation Sutherland-Hodgman convex polygon clipper and Vatti general clipping algorithm backed by unmanaged native memory.
- **Glacier.Graphics.Text**:
  - Direct TrueType / OpenType binary table parser (`head`, `maxp`, `loca`, `glyf`, `cmap` format 4 & 12, `hhea`, `hmtx`, `CFF `).
  - Sub-pixel LCD glyph rasterizer with sRGB gamma correction.
  - Dynamic LRU shelf-packing VRAM texture atlas.
- **Glacier.Graphics.Raster**:
  - CPU multi-core SIMD rasterizer (AVX-512 / Vector256 / scalar span-fill for BGRA32 / RGBA32).
  - Analytical trapezoidal coverage calculation.
  - Analytical tile compute rasterizer interface (16x16 binning, coarse classification, fine coverage evaluation).
- **Glacier.Graphics.Codecs**:
  - SIMD PNG encoder/decoder: vectorized Paeth/Sub/Up/Average filters, Deflate/Inflate stream processing.
  - JPEG baseline decoder with SIMD Arai-Agne-Barrowes (AAN) IDCT and vectorized YCbCr -> RGB color transform.
- **Glacier.Graphics.Pdf**:
  - High-performance vector PDF 1.7 / 2.0 exporter.
  - Flate-compressed content streams, TrueType font embedding, vector paths, fills, strokes, and ExtGState alpha transparency with blend modes.
- **High-Level `IGraphicsCanvas`**:
  - Unified canvas API for software rendering and vector PDF generation.

---

## License

MIT License.
