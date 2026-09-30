namespace Glacier.Graphics.Benchmarks;

using System;
using BenchmarkDotNet.Attributes;
using Glacier.Graphics.Vector;

[MemoryDiagnoser]
public class BezierBenchmarks
{
    private const int Points = 10_000;
    private float[] _t = null!;
    private float[] _outX = null!;
    private float[] _outY = null!;

    [GlobalSetup]
    public void Setup()
    {
        _t = new float[Points];
        for (int i = 0; i < Points; i++)
        {
            _t[i] = (float)i / (Points - 1);
        }
        _outX = new float[Points];
        _outY = new float[Points];
    }

    [Benchmark(Baseline = true)]
    public void Cubic_Scalar()
    {
        BezierKernels.EvaluateCubicScalar(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, _t, _outX, _outY);
    }

    [Benchmark]
    public void Cubic_Vector256()
    {
        BezierKernels.EvaluateCubicVector256(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, _t, _outX, _outY);
    }

    [Benchmark]
    public void Cubic_Avx512()
    {
        BezierKernels.EvaluateCubicAvx512(10f, 20f, 50f, 80f, 120f, 30f, 200f, 100f, _t, _outX, _outY);
    }
}
