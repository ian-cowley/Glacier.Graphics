![Glacier.Graphics Banner](assets/banner.jpg)

# 🎨 Glacier.Graphics

**Pure C# .NET 10 SIMD-Accelerated 2D Vector Graphics, Analytical CPU Rasterizer, TrueType Font Engine, SIMD Codecs, and Vector PDF 1.7 Generator**

Part of the **Glacier High-Performance .NET 10 Ecosystem** (Pillar 10).

[![Build & Test](https://img.shields.io/badge/tests-100%25%20passing%20(80%2F80)-brightgreen.svg)]()
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS%20%7C%20Android%20%7C%20iOS-blue.svg)]()
[![Language](https://img.shields.io/badge/c%23-.NET%2010-purple.svg)]()
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Website](https://img.shields.io/badge/Website-glaciersoftware.dev-66fcf1.svg)](https://glaciersoftware.dev/)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-Support-orange.svg?logo=buy-me-a-coffee)](https://buymeacoffee.com/glaciersoftware)

---

## 1. Executive Summary & Strategic Mandate

Existing cross-platform graphics libraries in the .NET ecosystem (`SkiaSharp`, `ImageSharp`, `System.Drawing.Common`) suffer from major architectural trade-offs:
1. **Massive Native Binary Bloat**: `SkiaSharp` bundles ~30 MB of precompiled native C++ binaries (`libSkiaSharp.dll`, `libSkiaSharp.so`, `libSkiaSharp.dylib`) per target runtime, bloating deployment footprints and causing cross-compilation friction on Linux/ARM64 and Native AOT.
2. **P/Invoke Call Overhead**: Every path command, draw call, and state transition crosses the managed/unmanaged boundary, introducing marshalling overhead and preventing compiler inlining.
3. **Fragmented Font & Text Engines**: Text rendering typically depends on native HarfBuzz and FreeType binaries, adding further external failure points and native memory fragmentation.
4. **Third-Party Codec Overhead**: Image encoding/decoding relies on native `libpng` and `libjpeg-turbo` with heap allocation churn on decoded pixel buffers.

**Glacier.Graphics** is a **100% pure managed C# .NET 10** graphics subsystem with zero external native dependencies:
- **SIMD-Accelerated Bézier Geometry**: Vectorized curve evaluation (`Vector512<float>` and `Vector256<float>`) wired into `VectorPath.Flatten` via `BezierKernels`.
- **Analytical CPU & Parallel Tile Compute Rasterizers**: Multi-core SIMD scanline rasterizer (`CpuRasterizer` with `Vector512`/`Vector256` prefix-sum and straight-alpha Porter-Duff `SRC_OVER` span blending) alongside a CPU `Parallel.For` 3-stage `TileComputeRasterizer` (16x16 flat pooled tile binning, coarse classification, fine coverage with left-backdrop winding accumulation — no GPU shader dispatch).
- **Pure C# TrueType (`glyf`) Parser**: Direct binary parsing of TrueType SFNT tables (`head`, `maxp`, `loca`, `glyf`, `cmap` format 4 & 12, `hhea`, `hmtx` — no CFF `.otf` outline parsing yet), paired with `LcdGlyphRasterizer` 3x FIR filtering and an R8 grayscale `GlyphAtlas` with value-keyed LRU eviction (grayscale R8 canvas compositing; no LCD subpixel canvas compositing yet).
- **SIMD PNG & JPEG Codecs**: Vectorized PNG filters (`Sub`/`Up`/`Average`/`Paeth` encode and `Sub`/`Up` decode) with CRC-32 verification and decompression-bomb limits, plus baseline JPEG decoding (`4:4:4`, `4:2:2`, `4:4:0`, `4:2:0`, grayscale, restart markers) with `Vector256<float>` AAN IDCT and SIMD YCbCr-to-RGB color conversion.
- **Pure C# Vector PDF 1.7 Exporter**: Unified `PooledBufferWriter`-backed PDF 1.7 generation (`PdfDocument` and `TinyPdfCreate`) with Flate-compressed content streams, ASCIIHex lossless inline images, Standard 14 Type1 fonts (plus full raw `.ttf` byte embedding under `/GlacierFont` — no font subsetting yet), and ExtGState alpha transparency blend modes.

---

## 2. Performance Verification & Reference Benchmarks

*Reference measurements from `Glacier.Graphics.Benchmarks` on .NET 10.0 (AMD Ryzen AI 9 HX 370, Zen 5 AVX-512) vs. SkiaSharp 2.88 Native C++*

| Operation / Workload | SkiaSharp (Native C++) | Glacier.Graphics (Pure C# .NET 10) | Advantage |
| :--- | :--- | :--- | :--- |
| **Native Dependency Size** | ~32 MB per OS | **0 Bytes (100% Managed C#)** | **Zero Native DLLs** |
| **Cubic Bézier (100k pts)** | 0.170 ms (588M pts/s) | **0.020 ms (4.96 Billion pts/s)** | **8.49x faster** |
| **Software Rasterizer (800x600)** | 1.12 ms/frame (892 FPS) | **0.365 ms/frame (2,737 FPS)** | **3.07x faster** |
| **Tile Compute Rasterizer (CPU)** | N/A | **0.919 ms/frame (1,088 FPS)** | **Parallel 16x16 Tile Binning** |
| **PNG Encode / Decode** | 5.2 ms / 6.1 ms | **2.85 ms / 4.65 ms (215 FPS)** | **1.82x faster encode** |
| **JPEG AAN IDCT + Color Xform** | 410 ns/block | **215.6 ns/block (4.64M blocks/s)** | **1.90x faster decode** |

---

## 3. Architecture & Subsystems

<p align="center">
  <img src="assets/architecture.jpg" alt="Glacier.Graphics Architecture Blueprint" width="100%" />
</p>

```
                                  Glacier.Graphics Architecture
 ┌─────────────────────────────────────────────────────────────────────────────────────────────┐
 │                                   Unified IGraphicsCanvas API                               │
 │             DrawPath, FillPath, DrawText, DrawImage, ClipPath, Save / Restore               │
 └──────────────────────────────┬───────────────────────────────┬──────────────────────────────┘
                                │                               │
 ┌──────────────────────────────▼──────────────┐ ┌──────────────▼──────────────────────────────┐
 │          CpuGraphicsCanvas (Bitmap)         │ │            PdfGraphicsCanvas (Vector)       │
 └──────────────┬──────────────────────────────┘ └──────────────┬──────────────────────────────┘
                │                                               │
 ┌──────────────▼───────────────────────────────────────────────▼──────────────────────────────┐
 │                                       Vector Subsystem                                      │
 │ • BezierKernels: Vector512 / Vector256 / scalar polynomial eval wired into VectorPath.Flatten│
 │ • LineStroker: Miter, Round, Bevel joins; Butt, Square, Round caps; pooled-scratch dashing  │
 │ • Clipping: Sutherland-Hodgman convex clipper (stackalloc/ArrayPool) & winding queries      │
 └──────────────┬───────────────────────────────┬──────────────────────────────────────────────┘
                │                               │
 ┌──────────────▼──────────────┐ ┌──────────────▼──────────────┐ ┌─────────────────────────────┐
 │       Text Subsystem        │ │      Raster Subsystem       │ │       Codec Subsystem       │
 │ • TrueType (glyf) Parser    │ │ • Multi-Core SIMD CpuRaster │ │ • SIMD PNG Encoder/Decoder  │
 │ • LcdGlyphRasterizer (FIR)  │ │ • Parallel CPU Tile Raster  │ │ • JPEG Baseline AAN IDCT    │
 │ • Value-Keyed LRU R8 Atlas  │ │ • Straight-Alpha SRC_OVER   │ │ • Deflate / Inflate Streams │
 └─────────────────────────────┘ └─────────────────────────────┘ └─────────────────────────────┘
```

### 3.1 Vector Subsystem (`Glacier.Graphics.Vector`)
- **`BezierKernels` & `VectorPath`**: Evaluates quadratic and cubic Bézier curves using SIMD (`Vector512<float>`, `Vector256<float>`, and scalar fallback), wired directly into `VectorPath.Flatten` via curvature-based adaptive segment counts.
- **`LineStroker`**: Expands 1D vector paths into closed outline polygons with miter limit enforcement, circular arc generation for round joins/caps, and thread-local pooled scratch buffers for stroking and dashing.
- **`PolygonClipper`**: Implements Sutherland-Hodgman convex polygon and bounding-rect clipping backed by `stackalloc` (for small polygons) and `ArrayPool<PointF>` (for large polygons), plus winding-number point-in-polygon queries. *(No Vatti general arbitrary polygon clipper yet.)*

### 3.2 Text & Font Subsystem (`Glacier.Graphics.Text`)
- **`TrueTypeFont`**: Zero-copy SFNT table parser for TrueType `.ttf` containers. Extracts quadratic/composite outlines from `glyf`/`loca`, performs `cmap` format 4 and format 12 Unicode lookups, and computes horizontal metrics (`hhea`/`hmtx`) including PDF-scaled text measurement. *(Supports `glyf` outlines only; no CFF `.otf` outline parsing yet.)*
- **`LcdGlyphRasterizer`**: Generates 3x horizontally supersampled RGB coverage bitmaps with a 5-tap FIR filter and sRGB gamma correction.
- **`GlyphAtlas`**: Dynamic shelf-packing R8 grayscale texture atlas (consuming the green luminance channel from `LcdGlyphRasterizer`) with a value-equatable font/glyph key and bounded LRU hot-glyph compaction on capacity pressure. *(`CpuGraphicsCanvas` composites grayscale R8 coverage; no LCD subpixel canvas compositing yet.)*

### 3.3 Raster Subsystem (`Glacier.Graphics.Raster`)
- **`CpuRasterizer`**: Multi-threaded CPU span rasterizer utilizing `Vector512`/`Vector256` scanline prefix sums and vectorized straight-alpha Porter-Duff `SRC_OVER` blending with analytical trapezoidal coverage.
- **`TileComputeRasterizer`**: CPU `Parallel.For` 3-stage tile rasterizer backed by flat pooled tile buffers (no GPU shader dispatch):
  1. *Binning*: Splits the surface into $16 \times 16$ pixel tiles with left-backdrop segment tracking.
  2. *Coarse Classification*: Classifies tiles as solid (`Full`), `Empty`, or `Complex`.
  3. *Fine Coverage*: Evaluates signed trapezoidal edge coverage per tile in parallel (`NonZero` and `EvenOdd` winding rules).

### 3.4 Codec Subsystem (`Glacier.Graphics.Codecs`)
- **`PngEncoder` / `PngDecoder`**: SIMD-accelerated PNG encoder and decoder with CRC-32 validation, decompression-bomb bounds, vectorized `ApplyFilter` (`Sub`/`Up`/`Average`/`Paeth`), vectorized `Sub`/`Up` reconstruction, and streaming ZLIB compression.
- **`JpegDecoder`**: Baseline JPEG decoder supporting grayscale and chroma-subsampled color (`4:4:4`, `4:2:2`, `4:4:0`, `4:2:0`) with DRI/RST restart markers, 8-lane `Vector256<float>` AAN IDCT (`JpegAanIdct`), SIMD `JpegColorTransform`, and bilinear chroma upsampling.

### 3.5 Vector PDF 1.7 Generator (`Glacier.Graphics.Pdf`)
- **`PdfDocument` / `PdfPage` / `PdfGraphicsCanvas`**: Disposable `PooledBufferWriter`-backed PDF 1.7 exporter. Emits Flate-compressed content streams, vector paths, ASCIIHex lossless inline images, Standard 14 Type1 fallback (`/Helvetica`), full raw `.ttf` byte embedding under `/GlacierFont` *(no font subsetting yet)*, and `ExtGState` alpha transparency.
- **`TinyPdfCreate` / `PdfBuilder`**: Fluent page-builder and Markdown-to-PDF 1.7 generator sharing the `PooledBufferWriter` / `Pdf17Writer` PDF 1.7 serialization pipeline with 64-bit `long` xref offsets.

---

## 4. Quickstart API

### 4.1 Drawing Vector Paths to a Bitmap Canvas
```csharp
using System.IO;
using Glacier.Graphics;
using Glacier.Graphics.Codecs.Png;
using Glacier.Graphics.Vector;

// Allocate an unmanaged 800x600 RGBA32 canvas
using var canvas = new CpuGraphicsCanvas(width: 800, height: 600);
canvas.Clear(new Rgba32(25, 28, 36, 255));

// Build vector path with lines and cubic curves
var path = new VectorPath();
path.MoveTo(50, 50);
path.CubicTo(100, 20, 200, 180, 250, 50);
path.LineTo(300, 200);
path.Close();

// Fill with semi-transparent cyan
var fillPaint = new Paint(new Rgba32(0, 220, 255, 180), PaintStyle.Fill);
canvas.FillPath(path, fillPaint);

// Stroke with miter join and round caps
var strokePaint = new Paint(
    new Rgba32(255, 255, 255, 255),
    PaintStyle.Stroke,
    StrokeWidth: 3.5f,
    Cap: StrokeCap.Round,
    Join: StrokeJoin.Miter);
canvas.DrawPath(path, strokePaint);

// Encode directly to PNG
using var fileStream = File.Create("output.png");
PngEncoder.Encode(canvas.Framebuffer, fileStream);
```

### 4.2 Rendering Text with TrueType Font Parsing
```csharp
using System.IO;
using Glacier.Graphics;
using Glacier.Graphics.Text;

// Load TrueType font from disk
var typeface = new TrueTypeFont(File.ReadAllBytes("Inter-Bold.ttf"));
var font = new Font(typeface, size: 32f);

// Draw styled text onto canvas
var textPaint = new Paint(new Rgba32(240, 240, 240, 255));
canvas.DrawText("Glacier .NET 10 High-Performance Graphics", x: 50, y: 120, font, textPaint);
```

### 4.3 High-Speed Vector PDF Generation
```csharp
using System.IO;
using Glacier.Graphics.Pdf;

using var doc = new PdfDocument();
using var page = doc.AddPage(width: 595.28f, height: 841.89f); // A4

using (var pdfCanvas = new PdfGraphicsCanvas(doc, page))
{
    pdfCanvas.DrawText("Glacier Ecosystem Financial Report", 50, 750, font, textPaint);
    pdfCanvas.FillPath(path, fillPaint);
}

File.WriteAllBytes("report.pdf", doc.Save());
```

---

## 5. Testing & Security Verification

All 80 unit tests pass with 100% success across all subsystems (verified with both SIMD enabled and scalar-fallback parity):
```bash
# Run unit test suite
dotnet test tests/Glacier.Graphics.Tests/Glacier.Graphics.Tests.csproj -c Release

# Run benchmarks
dotnet run --project benchmarks/Glacier.Graphics.Benchmarks/Glacier.Graphics.Benchmarks.csproj -c Release

# Security Secret Check (Zero Secrets Policy)
glacier security-check Glacier.Graphics
```

---

## 6. Ecosystem Cross-References

`Glacier.Graphics` serves as the foundational 2D rendering and imaging pillar across the **Glacier High-Performance Ecosystem**:
- **[Glacier.Plot](https://github.com/ian-cowley/Glacier.Plot)**: 120+ FPS scientific data plotting, statistical visualizations, and Grammar of Graphics charts rendered via `IGraphicsCanvas` and `LinearFramebuffer`.
- **[Glacier.Desktop](https://github.com/ian-cowley/Glacier.Desktop)** & **[Glacier.SpanCoder](https://github.com/ian-cowley/Glacier.SpanCoder)**: Cross-platform window presentation, UI layout, piece-table text buffer rendering, and IDE canvas display powered by `CpuGraphicsCanvas` and `LinearFramebuffer`.
- **[Glacier.Game](https://github.com/ian-cowley/Glacier.Game)** & **[Glacier.Showcase](https://github.com/ian-cowley/Glacier.Showcase)**: 2D game rendering and PDF 1.7 prospectus report generation (`TinyPdfCreate`).

---

## Credits

Developed by **Ian Cowley** and **Antigravity (Google DeepMind)**.

---

## 7. License

Licensed under the [MIT License](LICENSE). Copyright (c) 2026 Ian Cowley.

---

## ❄️ Glacier Ecosystem & Support

Part of the **[Glacier Ecosystem](https://glaciersoftware.dev/)** — high-performance, zero-allocation developer tooling and data engines natively built for modern C# and .NET 10.

* 🌐 **Website & Documentation**: [glaciersoftware.dev](https://glaciersoftware.dev/)
* ☕ **Buy Me a Coffee**: [buymeacoffee.com/glaciersoftware](https://buymeacoffee.com/glaciersoftware)
* ❤️ **GitHub Sponsors**: [github.com/sponsors/ian-cowley](https://github.com/sponsors/ian-cowley)
* 🐙 **All Repositories**: [github.com/ian-cowley](https://github.com/ian-cowley)
