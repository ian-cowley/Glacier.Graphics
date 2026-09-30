namespace Glacier.Graphics.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Glacier.Graphics;
using Glacier.Graphics.Codecs.Jpeg;

[MemoryDiagnoser]
public class JpegBenchmarks
{
    private float[] _idctBlock = null!;
    private float[] _yBlock = null!;
    private float[] _cbBlock = null!;
    private float[] _crBlock = null!;
    private Rgba32[] _rgbaBlock = null!;

    [GlobalSetup]
    public void Setup()
    {
        _idctBlock = new float[64];
        _yBlock = new float[64];
        _cbBlock = new float[64];
        _crBlock = new float[64];
        _rgbaBlock = new Rgba32[64];

        var rng = new Random(42);
        for (int i = 0; i < 64; i++)
        {
            _idctBlock[i] = rng.Next(-100, 100);
            _yBlock[i] = rng.Next(0, 255);
            _cbBlock[i] = rng.Next(0, 255);
            _crBlock[i] = rng.Next(0, 255);
        }
    }

    [Benchmark]
    public void AanIdct_8x8_Transform()
    {
        JpegAanIdct.Transform8x8(_idctBlock);
    }

    [Benchmark]
    public void ColorTransform_YCbCrToRGB_Block()
    {
        JpegColorTransform.ConvertBlock(_yBlock, _cbBlock, _crBlock, _rgbaBlock);
    }
}
