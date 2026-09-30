namespace Glacier.Graphics.Benchmarks;

using BenchmarkDotNet.Attributes;
using Glacier.Graphics;
using Glacier.Graphics.Raster;
using Glacier.Graphics.Vector;

[MemoryDiagnoser]
public class RasterizerBenchmarks
{
    private LinearFramebuffer _fb = null!;
    private VectorPath _path = null!;
    private CpuRasterizer _cpuRasterizer = null!;
    private TileComputeRasterizer _tileRasterizer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fb = new LinearFramebuffer(800, 600);
        _cpuRasterizer = new CpuRasterizer();
        _tileRasterizer = new TileComputeRasterizer();

        _path = new VectorPath();
        _path.AddOval(100, 100, 600, 400);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fb.Dispose();
    }

    [Benchmark(Baseline = true)]
    public void CpuRasterizer_FillPath()
    {
        _cpuRasterizer.FillPath(_fb, _path, Rgba32.Blue);
    }

    [Benchmark]
    public void TileCompute_Rasterize()
    {
        _tileRasterizer.Rasterize(_fb, _path, Rgba32.Blue);
    }
}
