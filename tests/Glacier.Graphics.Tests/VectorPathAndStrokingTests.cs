namespace Glacier.Graphics.Tests;

using System;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class VectorPathAndStrokingTests
{
    [Fact]
    public void VectorPath_AddRect_GeneratesCorrectBoundsAndVerbs()
    {
        var path = new VectorPath();
        path.AddRect(10, 20, 100, 50);

        Assert.Equal(5, path.VerbCount); // MoveTo, 3x LineTo, Close
        Assert.Equal(4, path.PointCount);

        RectF bounds = path.ComputeBounds();
        Assert.Equal(10f, bounds.Left);
        Assert.Equal(20f, bounds.Top);
        Assert.Equal(110f, bounds.Right);
        Assert.Equal(70f, bounds.Bottom);
        Assert.Equal(100f, bounds.Width);
        Assert.Equal(50f, bounds.Height);
    }

    [Fact]
    public void VectorPath_Transform_ScalesAndTranslates()
    {
        var path = new VectorPath();
        path.AddRect(0, 0, 10, 20);

        Matrix3x2 matrix = Matrix3x2.Multiply(Matrix3x2.CreateScale(2f, 3f), Matrix3x2.CreateTranslation(5f, 10f));
        path.Transform(matrix);

        RectF bounds = path.ComputeBounds();
        Assert.Equal(5f, bounds.Left);
        Assert.Equal(10f, bounds.Top);
        Assert.Equal(25f, bounds.Right);
        Assert.Equal(70f, bounds.Bottom);
    }

    [Fact]
    public void LineStroker_ButtCap_ProducesValidOutline()
    {
        var path = new VectorPath();
        path.MoveTo(0, 0);
        path.LineTo(100, 0);

        VectorPath stroked = LineStroker.Stroke(path, strokeWidth: 10f, cap: StrokeCap.Butt);

        Assert.True(stroked.VerbCount > 0);
        RectF bounds = stroked.ComputeBounds();
        Assert.True(bounds.Width >= 99f && bounds.Width <= 101f);
        Assert.True(bounds.Height >= 9f && bounds.Height <= 11f);
    }

    [Fact]
    public void LineStroker_SquareCap_ExtendsLengthByHalfWidth()
    {
        var path = new VectorPath();
        path.MoveTo(10, 0);
        path.LineTo(90, 0);

        VectorPath stroked = LineStroker.Stroke(path, strokeWidth: 10f, cap: StrokeCap.Square);

        RectF bounds = stroked.ComputeBounds();
        // Width should extend by 5 on each side: from 10-5=5 to 90+5=95 -> width 90
        Assert.True(bounds.Width >= 89f && bounds.Width <= 91f);
    }

    [Fact]
    public void LineStroker_MiterAndBevelJoins_Work()
    {
        var path = new VectorPath();
        path.MoveTo(0, 0);
        path.LineTo(50, 50);
        path.LineTo(100, 0);

        VectorPath miterStroked = LineStroker.Stroke(path, strokeWidth: 4f, join: StrokeJoin.Miter);
        VectorPath bevelStroked = LineStroker.Stroke(path, strokeWidth: 4f, join: StrokeJoin.Bevel);

        Assert.True(miterStroked.VerbCount > 0);
        Assert.True(bevelStroked.VerbCount > 0);
    }

    [Fact]
    public void LineStroker_Dashing_GeneratesMultipleSegments()
    {
        var path = new VectorPath();
        path.MoveTo(0, 0);
        path.LineTo(100, 0);

        float[] dashes = { 10f, 10f };
        VectorPath dashedStroked = LineStroker.Stroke(path, strokeWidth: 2f, dashPattern: dashes);

        Assert.True(dashedStroked.VerbCount > 4); // Multiple dashed sub-contours
    }
}
