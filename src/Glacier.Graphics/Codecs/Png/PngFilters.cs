namespace Glacier.Graphics.Codecs.Png;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

/// <summary>
/// Hardware-accelerated PNG scanline filtering and reconstruction.
/// Implements None, Sub, Up, Average, and Paeth predictors with SIMD vectorization.
/// </summary>
public static class PngFilters
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    /// <summary>
    /// Reconstructs filtered scanline in-place.
    /// </summary>
    public static void ReconstructScanline(
        byte filterType,
        Span<byte> currentScanline,
        ReadOnlySpan<byte> priorScanline,
        int bytesPerPixel)
    {
        int len = currentScanline.Length;

        switch (filterType)
        {
            case 0: // None
                break;

            case 1: // Sub: x' = x + a
            {
                for (int i = bytesPerPixel; i < len; i++)
                {
                    currentScanline[i] = (byte)(currentScanline[i] + currentScanline[i - bytesPerPixel]);
                }
                break;
            }

            case 2: // Up: x' = x + b
            {
                if (priorScanline.IsEmpty) break;

                int i = 0;
                if (Vector256.IsHardwareAccelerated && len >= Vector256<byte>.Count)
                {
                    int vecStep = Vector256<byte>.Count;
                    for (; i <= len - vecStep; i += vecStep)
                    {
                        var vCur = Vector256.Create(currentScanline.Slice(i, vecStep));
                        var vPrior = Vector256.Create(priorScanline.Slice(i, vecStep));
                        var vRes = vCur + vPrior;
                        vRes.CopyTo(currentScanline.Slice(i, vecStep));
                    }
                }

                for (; i < len; i++)
                {
                    currentScanline[i] = (byte)(currentScanline[i] + priorScanline[i]);
                }
                break;
            }

            case 3: // Average: x' = x + floor((a + b) / 2)
            {
                for (int i = 0; i < len; i++)
                {
                    int a = i >= bytesPerPixel ? currentScanline[i - bytesPerPixel] : 0;
                    int b = !priorScanline.IsEmpty ? priorScanline[i] : 0;
                    currentScanline[i] = (byte)(currentScanline[i] + ((a + b) >> 1));
                }
                break;
            }

            case 4: // Paeth: x' = x + PaethPredictor(a, b, c)
            {
                for (int i = 0; i < len; i++)
                {
                    byte a = i >= bytesPerPixel ? currentScanline[i - bytesPerPixel] : (byte)0;
                    byte b = !priorScanline.IsEmpty ? priorScanline[i] : (byte)0;
                    byte c = (!priorScanline.IsEmpty && i >= bytesPerPixel) ? priorScanline[i - bytesPerPixel] : (byte)0;
                    currentScanline[i] = (byte)(currentScanline[i] + PaethPredictor(a, b, c));
                }
                break;
            }

            default:
                throw new FormatException($"Invalid PNG filter type: {filterType}");
        }
    }

    /// <summary>
    /// Applies PNG filter during encoding.
    /// </summary>
    public static void ApplyFilter(
        byte filterType,
        ReadOnlySpan<byte> currentScanline,
        ReadOnlySpan<byte> priorScanline,
        Span<byte> outputScanline,
        int bytesPerPixel)
    {
        int len = currentScanline.Length;

        switch (filterType)
        {
            case 0: // None
                currentScanline.CopyTo(outputScanline);
                break;

            case 1: // Sub: raw - a
                for (int i = 0; i < len; i++)
                {
                    int a = i >= bytesPerPixel ? currentScanline[i - bytesPerPixel] : 0;
                    outputScanline[i] = (byte)(currentScanline[i] - a);
                }
                break;

            case 2: // Up: raw - b
                for (int i = 0; i < len; i++)
                {
                    int b = !priorScanline.IsEmpty ? priorScanline[i] : 0;
                    outputScanline[i] = (byte)(currentScanline[i] - b);
                }
                break;

            case 3: // Average
                for (int i = 0; i < len; i++)
                {
                    int a = i >= bytesPerPixel ? currentScanline[i - bytesPerPixel] : 0;
                    int b = !priorScanline.IsEmpty ? priorScanline[i] : 0;
                    outputScanline[i] = (byte)(currentScanline[i] - ((a + b) >> 1));
                }
                break;

            case 4: // Paeth
                for (int i = 0; i < len; i++)
                {
                    byte a = i >= bytesPerPixel ? currentScanline[i - bytesPerPixel] : (byte)0;
                    byte b = !priorScanline.IsEmpty ? priorScanline[i] : (byte)0;
                    byte c = (!priorScanline.IsEmpty && i >= bytesPerPixel) ? priorScanline[i - bytesPerPixel] : (byte)0;
                    outputScanline[i] = (byte)(currentScanline[i] - PaethPredictor(a, b, c));
                }
                break;
        }
    }
}
