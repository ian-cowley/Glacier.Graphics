namespace Glacier.Graphics.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Glacier.Graphics;
using Glacier.Graphics.Codecs.Png;
using Glacier.Graphics.Raster;

[MemoryDiagnoser]
public class PngCodecBenchmarks
{
    private LinearFramebuffer _fb = null!;
    private byte[] _pngBytes = null!;
    private byte[] _scanline = null!;
    private byte[] _priorScanline = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fb = new LinearFramebuffer(512, 512);
        for (int y = 0; y < 512; y++)
        {
            for (int x = 0; x < 512; x++)
            {
                _fb.SetPixel(x, y, new Rgba32((byte)x, (byte)y, (byte)(x + y), 255));
            }
        }
        _pngBytes = PngEncoder.Encode(_fb);

        _scanline = new byte[512 * 4];
        _priorScanline = new byte[512 * 4];
        new Random(42).NextBytes(_scanline);
        new Random(43).NextBytes(_priorScanline);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fb.Dispose();
    }

    [Benchmark]
    public byte[] Png_Encode_512x512()
    {
        return PngEncoder.Encode(_fb);
    }

    [Benchmark]
    public LinearFramebuffer Png_Decode_512x512()
    {
        using var fb = PngDecoder.Decode(_pngBytes);
        return fb;
    }

    [Benchmark]
    public void Png_Reconstruct_UpFilter()
    {
        PngFilters.ReconstructScanline(2, _scanline, _priorScanline, bytesPerPixel: 4);
    }

    [Benchmark]
    public void Png_Reconstruct_PaethFilter()
    {
        PngFilters.ReconstructScanline(4, _scanline, _priorScanline, bytesPerPixel: 4);
    }
}
