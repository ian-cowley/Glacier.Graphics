namespace Glacier.Graphics.Tests;

using System;
using Glacier.Graphics;
using Glacier.Graphics.Codecs.Png;
using Glacier.Graphics.Raster;
using Xunit;

public sealed class PngCodecTests
{
    [Fact]
    public void PngFilters_PaethPredictor_MatchesSpecification()
    {
        // Spec test vectors
        Assert.Equal(10, PngFilters.PaethPredictor(10, 20, 20));
        Assert.Equal(20, PngFilters.PaethPredictor(10, 20, 10));
        Assert.Equal(30, PngFilters.PaethPredictor(30, 30, 30));
    }

    [Fact]
    public void PngFilters_SubFilter_Roundtrips()
    {
        byte[] original = { 10, 20, 30, 40, 50, 60, 70, 80 };
        byte[] filtered = new byte[original.Length];
        byte[] reconstructed = new byte[original.Length];

        PngFilters.ApplyFilter(1, original, ReadOnlySpan<byte>.Empty, filtered, bytesPerPixel: 4);
        filtered.CopyTo(reconstructed, 0);
        PngFilters.ReconstructScanline(1, reconstructed, ReadOnlySpan<byte>.Empty, bytesPerPixel: 4);

        Assert.Equal(original, reconstructed);
    }

    [Fact]
    public void PngFilters_UpFilter_Roundtrips()
    {
        byte[] prior = { 10, 10, 10, 10 };
        byte[] original = { 30, 40, 50, 60 };
        byte[] filtered = new byte[original.Length];
        byte[] reconstructed = new byte[original.Length];

        PngFilters.ApplyFilter(2, original, prior, filtered, bytesPerPixel: 4);
        filtered.CopyTo(reconstructed, 0);
        PngFilters.ReconstructScanline(2, reconstructed, prior, bytesPerPixel: 4);

        Assert.Equal(original, reconstructed);
    }

    [Fact]
    public void PngEncoderDecoder_LosslessRoundtrip()
    {
        int width = 32;
        int height = 32;

        using var srcFb = new LinearFramebuffer(width, height);
        // Fill with a colorful pattern
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x * 8);
                byte g = (byte)(y * 8);
                byte b = (byte)((x + y) * 4);
                srcFb.SetPixel(x, y, new Rgba32(r, g, b, 255));
            }
        }

        byte[] pngBytes = PngEncoder.Encode(srcFb);
        Assert.True(pngBytes.Length > 0);

        using var dstFb = PngDecoder.Decode(pngBytes);
        Assert.Equal(width, dstFb.Width);
        Assert.Equal(height, dstFb.Height);

        // Verify 100% exact pixel match
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.Equal(srcFb.GetPixel(x, y), dstFb.GetPixel(x, y));
            }
        }
    }
}
