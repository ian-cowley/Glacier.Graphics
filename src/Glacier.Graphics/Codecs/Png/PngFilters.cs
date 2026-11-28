namespace Glacier.Graphics.Codecs.Png;

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
                int i = bytesPerPixel;
                if (bytesPerPixel == 4 && Sse2.IsSupported && len >= 16)
                {
                    var prevBroadcast = Vector128.Create(
                        System.Runtime.InteropServices.MemoryMarshal.Read<uint>(currentScanline)).AsByte();
                    // Process first 4 bytes already done; start at i = 4
                    // Align by doing 16-byte chunks from i = 4
                    for (; i <= len - 16; i += 16)
                    {
                        var x = Vector128.Create(currentScanline.Slice(i, 16));
                        x += Sse2.ShiftLeftLogical128BitLane(x, 4);
                        x += Sse2.ShiftLeftLogical128BitLane(x, 8);
                        x += prevBroadcast;
                        x.CopyTo(currentScanline.Slice(i, 16));
                        prevBroadcast = Sse2.Shuffle(x.AsUInt32(), 0xFF).AsByte();
                    }
                }

                for (; i < len; i++)
                {
                    currentScanline[i] = (byte)(currentScanline[i] + currentScanline[i - bytesPerPixel]);
                }
                break;
            }

            case 2: // Up: x' = x + b
            {
                if (priorScanline.IsEmpty) break;

                int i = 0;
                if (Vector512.IsHardwareAccelerated && len >= Vector512<byte>.Count)
                {
                    int vecStep = Vector512<byte>.Count;
                    for (; i <= len - vecStep; i += vecStep)
                    {
                        var vCur = Vector512.Create(currentScanline.Slice(i, vecStep));
                        var vPrior = Vector512.Create(priorScanline.Slice(i, vecStep));
                        (vCur + vPrior).CopyTo(currentScanline.Slice(i, vecStep));
                    }
                }

                if (Vector256.IsHardwareAccelerated && len - i >= Vector256<byte>.Count)
                {
                    int vecStep = Vector256<byte>.Count;
                    for (; i <= len - vecStep; i += vecStep)
                    {
                        var vCur = Vector256.Create(currentScanline.Slice(i, vecStep));
                        var vPrior = Vector256.Create(priorScanline.Slice(i, vecStep));
                        (vCur + vPrior).CopyTo(currentScanline.Slice(i, vecStep));
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
                int firstCount = Math.Min(bytesPerPixel, len);
                for (int i = 0; i < firstCount; i++)
                {
                    int b = !priorScanline.IsEmpty ? priorScanline[i] : 0;
                    currentScanline[i] = (byte)(currentScanline[i] + (b >> 1));
                }

                if (priorScanline.IsEmpty)
                {
                    for (int i = bytesPerPixel; i < len; i++)
                    {
                        currentScanline[i] = (byte)(currentScanline[i] + (currentScanline[i - bytesPerPixel] >> 1));
                    }
                }
                else
                {
                    for (int i = bytesPerPixel; i < len; i++)
                    {
                        int a = currentScanline[i - bytesPerPixel];
                        int b = priorScanline[i];
                        currentScanline[i] = (byte)(currentScanline[i] + ((a + b) >> 1));
                    }
                }
                break;
            }

            case 4: // Paeth: x' = x + PaethPredictor(a, b, c)
            {
                int firstCount = Math.Min(bytesPerPixel, len);
                for (int i = 0; i < firstCount; i++)
                {
                    byte b = !priorScanline.IsEmpty ? priorScanline[i] : (byte)0;
                    currentScanline[i] = (byte)(currentScanline[i] + b);
                }

                if (priorScanline.IsEmpty)
                {
                    for (int i = bytesPerPixel; i < len; i++)
                    {
                        currentScanline[i] = (byte)(currentScanline[i] + currentScanline[i - bytesPerPixel]);
                    }
                }
                else
                {
                    for (int i = bytesPerPixel; i < len; i++)
                    {
                        byte a = currentScanline[i - bytesPerPixel];
                        byte b = priorScanline[i];
                        byte c = priorScanline[i - bytesPerPixel];
                        currentScanline[i] = (byte)(currentScanline[i] + PaethPredictor(a, b, c));
                    }
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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
            {
                int firstCount = Math.Min(bytesPerPixel, len);
                currentScanline.Slice(0, firstCount).CopyTo(outputScanline);

                int i = firstCount;
                if (Vector512.IsHardwareAccelerated && len - i >= Vector512<byte>.Count)
                {
                    int vecStep = Vector512<byte>.Count;
                    for (; i <= len - vecStep; i += vecStep)
                    {
                        var vCur = Vector512.Create(currentScanline.Slice(i, vecStep));
                        var vLeft = Vector512.Create(currentScanline.Slice(i - bytesPerPixel, vecStep));
                        (vCur - vLeft).CopyTo(outputScanline.Slice(i, vecStep));
                    }
                }

                if (Vector256.IsHardwareAccelerated && len - i >= Vector256<byte>.Count)
                {
                    int vecStep = Vector256<byte>.Count;
                    for (; i <= len - vecStep; i += vecStep)
                    {
                        var vCur = Vector256.Create(currentScanline.Slice(i, vecStep));
                        var vLeft = Vector256.Create(currentScanline.Slice(i - bytesPerPixel, vecStep));
                        (vCur - vLeft).CopyTo(outputScanline.Slice(i, vecStep));
                    }
                }

                for (; i < len; i++)
                {
                    outputScanline[i] = (byte)(currentScanline[i] - currentScanline[i - bytesPerPixel]);
                }
                break;
            }

            case 2: // Up: raw - b
            {
                if (priorScanline.IsEmpty)
                {
                    currentScanline.CopyTo(outputScanline);
                    break;
                }

                int i = 0;
                if (Vector512.IsHardwareAccelerated && len >= Vector512<byte>.Count)
                {
                    int vecStep = Vector512<byte>.Count;
                    for (; i <= len - vecStep; i += vecStep)
                    {
                        var vCur = Vector512.Create(currentScanline.Slice(i, vecStep));
                        var vPrior = Vector512.Create(priorScanline.Slice(i, vecStep));
                        (vCur - vPrior).CopyTo(outputScanline.Slice(i, vecStep));
                    }
                }

                if (Vector256.IsHardwareAccelerated && len - i >= Vector256<byte>.Count)
                {
                    int vecStep = Vector256<byte>.Count;
                    for (; i <= len - vecStep; i += vecStep)
                    {
                        var vCur = Vector256.Create(currentScanline.Slice(i, vecStep));
                        var vPrior = Vector256.Create(priorScanline.Slice(i, vecStep));
                        (vCur - vPrior).CopyTo(outputScanline.Slice(i, vecStep));
                    }
                }

                for (; i < len; i++)
                {
                    outputScanline[i] = (byte)(currentScanline[i] - priorScanline[i]);
                }
                break;
            }

            case 3: // Average: raw - floor((a + b) / 2)
            {
                int firstCount = Math.Min(bytesPerPixel, len);
                for (int i = 0; i < firstCount; i++)
                {
                    int b = !priorScanline.IsEmpty ? priorScanline[i] : 0;
                    outputScanline[i] = (byte)(currentScanline[i] - (b >> 1));
                }

                int idx = firstCount;
                if (priorScanline.IsEmpty)
                {
                    if (Vector256.IsHardwareAccelerated && len - idx >= Vector256<byte>.Count)
                    {
                        int vecStep = Vector256<byte>.Count;
                        var v7F = Vector256.Create((byte)0x7F);
                        for (; idx <= len - vecStep; idx += vecStep)
                        {
                            var vCur = Vector256.Create(currentScanline.Slice(idx, vecStep));
                            var vA = Vector256.Create(currentScanline.Slice(idx - bytesPerPixel, vecStep));
                            var vHalfA = (vA.AsUInt16() >>> 1).AsByte() & v7F;
                            (vCur - vHalfA).CopyTo(outputScanline.Slice(idx, vecStep));
                        }
                    }

                    for (; idx < len; idx++)
                    {
                        outputScanline[idx] = (byte)(currentScanline[idx] - (currentScanline[idx - bytesPerPixel] >> 1));
                    }
                }
                else
                {
                    if (Vector512.IsHardwareAccelerated && len - idx >= Vector512<byte>.Count)
                    {
                        int vecStep = Vector512<byte>.Count;
                        var v7F = Vector512.Create((byte)0x7F);
                        for (; idx <= len - vecStep; idx += vecStep)
                        {
                            var vCur = Vector512.Create(currentScanline.Slice(idx, vecStep));
                            var vA = Vector512.Create(currentScanline.Slice(idx - bytesPerPixel, vecStep));
                            var vB = Vector512.Create(priorScanline.Slice(idx, vecStep));
                            var vAvg = (vA & vB) + (((vA ^ vB).AsUInt16() >>> 1).AsByte() & v7F);
                            (vCur - vAvg).CopyTo(outputScanline.Slice(idx, vecStep));
                        }
                    }

                    if (Vector256.IsHardwareAccelerated && len - idx >= Vector256<byte>.Count)
                    {
                        int vecStep = Vector256<byte>.Count;
                        var v7F = Vector256.Create((byte)0x7F);
                        for (; idx <= len - vecStep; idx += vecStep)
                        {
                            var vCur = Vector256.Create(currentScanline.Slice(idx, vecStep));
                            var vA = Vector256.Create(currentScanline.Slice(idx - bytesPerPixel, vecStep));
                            var vB = Vector256.Create(priorScanline.Slice(idx, vecStep));
                            var vAvg = (vA & vB) + (((vA ^ vB).AsUInt16() >>> 1).AsByte() & v7F);
                            (vCur - vAvg).CopyTo(outputScanline.Slice(idx, vecStep));
                        }
                    }

                    for (; idx < len; idx++)
                    {
                        int a = currentScanline[idx - bytesPerPixel];
                        int b = priorScanline[idx];
                        outputScanline[idx] = (byte)(currentScanline[idx] - ((a + b) >> 1));
                    }
                }
                break;
            }

            case 4: // Paeth: raw - PaethPredictor(a, b, c)
            {
                int firstCount = Math.Min(bytesPerPixel, len);
                for (int i = 0; i < firstCount; i++)
                {
                    byte b = !priorScanline.IsEmpty ? priorScanline[i] : (byte)0;
                    outputScanline[i] = (byte)(currentScanline[i] - b);
                }

                int idx = firstCount;
                if (priorScanline.IsEmpty)
                {
                    for (; idx < len; idx++)
                    {
                        outputScanline[idx] = (byte)(currentScanline[idx] - currentScanline[idx - bytesPerPixel]);
                    }
                }
                else
                {
                    if (Vector256.IsHardwareAccelerated && len - idx >= Vector256<byte>.Count)
                    {
                        int vecStep = Vector256<byte>.Count;
                        for (; idx <= len - vecStep; idx += vecStep)
                        {
                            var vCur = Vector256.Create(currentScanline.Slice(idx, vecStep));
                            var vA = Vector256.Create(currentScanline.Slice(idx - bytesPerPixel, vecStep));
                            var vB = Vector256.Create(priorScanline.Slice(idx, vecStep));
                            var vC = Vector256.Create(priorScanline.Slice(idx - bytesPerPixel, vecStep));

                            var (aLo, aHi) = Vector256.Widen(vA);
                            var (bLo, bHi) = Vector256.Widen(vB);
                            var (cLo, cHi) = Vector256.Widen(vC);

                            var predLo = PaethPredict16(aLo.AsInt16(), bLo.AsInt16(), cLo.AsInt16()).AsUInt16();
                            var predHi = PaethPredict16(aHi.AsInt16(), bHi.AsInt16(), cHi.AsInt16()).AsUInt16();

                            var vPred = Vector256.Narrow(predLo, predHi);
                            (vCur - vPred).CopyTo(outputScanline.Slice(idx, vecStep));
                        }
                    }

                    for (; idx < len; idx++)
                    {
                        byte a = currentScanline[idx - bytesPerPixel];
                        byte b = priorScanline[idx];
                        byte c = priorScanline[idx - bytesPerPixel];
                        outputScanline[idx] = (byte)(currentScanline[idx] - PaethPredictor(a, b, c));
                    }
                }
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> PaethPredict16(Vector256<short> a, Vector256<short> b, Vector256<short> c)
    {
        var p = a + b - c;
        var pa = Vector256.Abs(p - a);
        var pb = Vector256.Abs(p - b);
        var pc = Vector256.Abs(p - c);

        var maskA = ~Vector256.GreaterThan(pa, pb) & ~Vector256.GreaterThan(pa, pc);
        var maskB = ~Vector256.GreaterThan(pb, pc);
        return Vector256.ConditionalSelect(maskA, a, Vector256.ConditionalSelect(maskB, b, c));
    }
}
