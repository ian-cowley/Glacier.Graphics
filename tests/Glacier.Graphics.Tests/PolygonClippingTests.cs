namespace Glacier.Graphics.Tests;

using System;
using Glacier.Graphics.Vector;
using Xunit;

public sealed class PolygonClippingTests
{
    [Fact]
    public void ClipToRect_ClipsSubjectPolygon()
    {
        PointF[] subject =
        {
            new(-10f, 50f),
            new(110f, 50f),
            new(50f, 150f)
        };

        RectF clipRect = RectF.FromLTRB(0, 0, 100, 100);
        Span<PointF> output = stackalloc PointF[16];

        int count = PolygonClipper.ClipToRect(subject, clipRect, output);

        Assert.True(count >= 3);
        for (int i = 0; i < count; i++)
        {
            Assert.True(output[i].X >= -0.01f && output[i].X <= 100.01f);
            Assert.True(output[i].Y >= -0.01f && output[i].Y <= 100.01f);
        }
    }

    [Fact]
    public void WindingRule_PointInPolygon_Tests()
    {
        PointF[] square =
        {
            new(0f, 0f),
            new(100f, 0f),
            new(100f, 100f),
            new(0f, 100f)
        };

        PointF inside = new(50f, 50f);
        PointF outside = new(150f, 50f);

        Assert.True(PolygonClipper.IsPointInPolygon(inside, square, WindingRule.NonZero));
        Assert.True(PolygonClipper.IsPointInPolygon(inside, square, WindingRule.EvenOdd));

        Assert.False(PolygonClipper.IsPointInPolygon(outside, square, WindingRule.NonZero));
        Assert.False(PolygonClipper.IsPointInPolygon(outside, square, WindingRule.EvenOdd));
    }
}
