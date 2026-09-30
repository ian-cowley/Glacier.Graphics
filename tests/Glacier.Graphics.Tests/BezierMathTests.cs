namespace Glacier.Graphics.Tests;

using System;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class BezierMathTests
{
    [Fact]
    public void CubicBezier_Endpoints_MatchControlPoints()
    {
        float p0x = 10f, p0y = 20f;
        float p1x = 30f, p1y = 40f;
        float p2x = 50f, p2y = 60f;
        float p3x = 70f, p3y = 80f;

        float[] t = { 0.0f, 1.0f };
        float[] outX = new float[2];
        float[] outY = new float[2];

        BezierKernels.EvaluateCubic(p0x, p0y, p1x, p1y, p2x, p2y, p3x, p3y, t, outX, outY);

        Assert.Equal(p0x, outX[0], precision: 5);
        Assert.Equal(p0y, outY[0], precision: 5);
        Assert.Equal(p3x, outX[1], precision: 5);
        Assert.Equal(p3y, outY[1], precision: 5);
    }

    [Fact]
    public void CubicBezier_SIMD_MatchesScalarFallback()
    {
        float p0x = 12.5f, p0y = 25.0f;
        float p1x = 45.0f, p1y = 85.0f;
        float p2x = 110.0f, p2y = 30.0f;
        float p3x = 150.0f, p3y = 90.0f;

        const int count = 64;
        float[] t = new float[count];
        for (int i = 0; i < count; i++)
        {
            t[i] = (float)i / (count - 1);
        }

        float[] scalarX = new float[count];
        float[] scalarY = new float[count];
        float[] simdX = new float[count];
        float[] simdY = new float[count];

        BezierKernels.EvaluateCubicScalar(p0x, p0y, p1x, p1y, p2x, p2y, p3x, p3y, t, scalarX, scalarY);
        BezierKernels.EvaluateCubicVector256(p0x, p0y, p1x, p1y, p2x, p2y, p3x, p3y, t, simdX, simdY);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(scalarX[i], simdX[i], precision: 3);
            Assert.Equal(scalarY[i], simdY[i], precision: 3);
        }
    }

    [Fact]
    public void QuadraticBezier_SIMD_MatchesScalarFallback()
    {
        float p0x = 5f, p0y = 10f;
        float p1x = 25f, p1y = 60f;
        float p2x = 80f, p2y = 20f;

        const int count = 32;
        float[] t = new float[count];
        for (int i = 0; i < count; i++) t[i] = (float)i / (count - 1);

        float[] scalarX = new float[count];
        float[] scalarY = new float[count];
        float[] simdX = new float[count];
        float[] simdY = new float[count];

        BezierKernels.EvaluateQuadraticScalar(p0x, p0y, p1x, p1y, p2x, p2y, t, scalarX, scalarY);
        BezierKernels.EvaluateQuadraticVector256(p0x, p0y, p1x, p1y, p2x, p2y, t, simdX, simdY);

        for (int i = 0; i < count; i++)
        {
            Assert.Equal(scalarX[i], simdX[i], precision: 4);
            Assert.Equal(scalarY[i], simdY[i], precision: 4);
        }
    }
}
