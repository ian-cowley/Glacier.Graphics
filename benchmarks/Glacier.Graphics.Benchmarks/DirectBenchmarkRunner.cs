namespace Glacier.Graphics.Benchmarks;

using System;
using System.Diagnostics;
using Glacier.Graphics;
using Glacier.Graphics.Codecs.Jpeg;
using Glacier.Graphics.Codecs.Png;
using Glacier.Graphics.Raster;
using Glacier.Graphics.Vector;

public static class DirectBenchmarkRunner
{
    public static void RunAll()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("       GLACIER.GRAPHICS (PILLAR 10) PERFORMANCE VERIFICATION SUITE              ");
        Console.WriteLine("================================================================================");

        // 1. Bezier Cubic Evaluation
        Console.WriteLine("\n[1] Cubic Bezier Curve SIMD Evaluation (100,000 points):");
        const int points = 100_000;
        var t = new float[points];
        for (int i = 0; i < points; i++) t[i] = (float)i / (points - 1);
        var outX = new float[points];
        var outY = new float[points];

        // Warmup
        for (int w = 0; w < 100; w++)
        {
            BezierKernels.EvaluateCubicScalar(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, t, outX, outY);
            BezierKernels.EvaluateCubicVector256(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, t, outX, outY);
            BezierKernels.EvaluateCubicAvx512(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, t, outX, outY);
        }

        const int bezierIters = 200;
        var swScalar = Stopwatch.StartNew();
        for (int it = 0; it < bezierIters; it++)
            BezierKernels.EvaluateCubicScalar(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, t, outX, outY);
        swScalar.Stop();
        double msScalar = swScalar.Elapsed.TotalMilliseconds / bezierIters;

        var swVec = Stopwatch.StartNew();
        for (int it = 0; it < bezierIters; it++)
            BezierKernels.EvaluateCubicVector256(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, t, outX, outY);
        swVec.Stop();
        double msVec = swVec.Elapsed.TotalMilliseconds / bezierIters;

        var swAvx = Stopwatch.StartNew();
        for (int it = 0; it < bezierIters; it++)
            BezierKernels.EvaluateCubicAvx512(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, t, outX, outY);
        swAvx.Stop();
        double msAvx = swAvx.Elapsed.TotalMilliseconds / bezierIters;

        Console.WriteLine($"  - Scalar Baseline:    {msScalar:F3} ms ({(points / msScalar) / 1000.0:F1} M pts/s)");
        Console.WriteLine($"  - Vector256 (AVX2):   {msVec:F3} ms ({(points / msVec) / 1000.0:F1} M pts/s) -> {msScalar / msVec:F2}x speedup");
        Console.WriteLine($"  - AVX-512 FMA:        {msAvx:F3} ms ({(points / msAvx) / 1000.0:F1} M pts/s) -> {msScalar / msAvx:F2}x speedup");

        // 2. Scanline & Tile Rasterizer
        Console.WriteLine("\n[2] Software Rasterizer Frame Render (800x600 Framebuffer):");
        using var fb = new LinearFramebuffer(800, 600);
        var path = new VectorPath();
        path.AddOval(100, 100, 600, 400);
        var cpuRaster = new CpuRasterizer();
        var tileRaster = new TileComputeRasterizer();

        for (int w = 0; w < 50; w++)
        {
            cpuRaster.FillPath(fb, path, Rgba32.Blue);
            tileRaster.Rasterize(fb, path, Rgba32.Blue);
        }

        const int rasterIters = 100;
        var swCpu = Stopwatch.StartNew();
        for (int it = 0; it < rasterIters; it++)
            cpuRaster.FillPath(fb, path, Rgba32.Blue);
        swCpu.Stop();
        double msCpu = swCpu.Elapsed.TotalMilliseconds / rasterIters;

        var swTile = Stopwatch.StartNew();
        for (int it = 0; it < rasterIters; it++)
            tileRaster.Rasterize(fb, path, Rgba32.Blue);
        swTile.Stop();
        double msTile = swTile.Elapsed.TotalMilliseconds / rasterIters;

        Console.WriteLine($"  - CpuRasterizer Fill:     {msCpu:F3} ms/frame ({1000.0 / msCpu:F1} FPS)");
        Console.WriteLine($"  - TileCompute Rasterizer: {msTile:F3} ms/frame ({1000.0 / msTile:F1} FPS)");

        // 3. PNG Codec
        Console.WriteLine("\n[3] Pure C# PNG Codec Throughput (512x512 Image):");
        using var pngFb = new LinearFramebuffer(512, 512);
        for (int y = 0; y < 512; y++)
            for (int x = 0; x < 512; x++)
                pngFb.SetPixel(x, y, new Rgba32((byte)x, (byte)y, (byte)(x + y), 255));

        byte[] encodedPng = PngEncoder.Encode(pngFb);

        const int pngIters = 50;
        var swEncode = Stopwatch.StartNew();
        for (int it = 0; it < pngIters; it++)
            _ = PngEncoder.Encode(pngFb);
        swEncode.Stop();
        double msEncode = swEncode.Elapsed.TotalMilliseconds / pngIters;

        var swDecode = Stopwatch.StartNew();
        for (int it = 0; it < pngIters; it++)
        {
            using var dec = PngDecoder.Decode(encodedPng);
        }
        swDecode.Stop();
        double msDecode = swDecode.Elapsed.TotalMilliseconds / pngIters;

        Console.WriteLine($"  - PNG Encode:  {msEncode:F2} ms/frame (Compressed size: {encodedPng.Length / 1024.0:F1} KB)");
        Console.WriteLine($"  - PNG Decode:  {msDecode:F2} ms/frame ({1000.0 / msDecode:F1} frames/sec)");

        // 4. JPEG 8x8 DCT / IDCT
        Console.WriteLine("\n[4] JPEG AAN IDCT & Color Transform (64-sample block):");
        var idctBlock = new float[64];
        var yBlock = new float[64];
        var cbBlock = new float[64];
        var crBlock = new float[64];
        var rgbaBlock = new Rgba32[64];

        const int jpegIters = 1_000_000;
        var swIdct = Stopwatch.StartNew();
        for (int it = 0; it < jpegIters; it++)
            JpegAanIdct.Transform8x8(idctBlock);
        swIdct.Stop();
        double nsIdct = (swIdct.Elapsed.TotalMilliseconds * 1_000_000.0) / jpegIters;

        var swColor = Stopwatch.StartNew();
        for (int it = 0; it < jpegIters; it++)
            JpegColorTransform.ConvertBlock(yBlock, cbBlock, crBlock, rgbaBlock);
        swColor.Stop();
        double nsColor = (swColor.Elapsed.TotalMilliseconds * 1_000_000.0) / jpegIters;

        Console.WriteLine($"  - AAN IDCT 8x8 Transform: {nsIdct:F1} ns/block ({1_000_000_000.0 / nsIdct / 1e6:F2} M blocks/sec)");
        Console.WriteLine($"  - YCbCr -> RGBA Convert:  {nsColor:F1} ns/block ({1_000_000_000.0 / nsColor / 1e6:F2} M blocks/sec)");

        Console.WriteLine("================================================================================");
        Console.WriteLine("           ALL GRAPHICS BENCHMARKS COMPLETED SUCCESSFULLY                       ");
        Console.WriteLine("================================================================================");
    }
}
