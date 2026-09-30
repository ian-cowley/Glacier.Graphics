namespace Glacier.Graphics.Codecs.Jpeg;

using System;
using System.IO;

/// <summary>
/// Huffman decoding table for JPEG baseline DCT decoding.
/// </summary>
public sealed class JpegHuffmanTable
{
    public byte[] Bits { get; } = new byte[16];
    public byte[] Values { get; private set; } = Array.Empty<byte>();

    // Fast lookup table for codes <= 8 bits: (symbol << 8) | codeLength
    private readonly short[] _lookup = new short[256];
    private readonly int[] _minCode = new int[17];
    private readonly int[] _maxCode = new int[17];
    private readonly int[] _valOffset = new int[17];

    public void Initialize(ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        bits.CopyTo(Bits);
        Values = values.ToArray();

        Array.Fill(_lookup, (short)-1);

        int code = 0;
        int valIdx = 0;

        for (int len = 1; len <= 16; len++)
        {
            int count = Bits[len - 1];
            if (count > 0)
            {
                _minCode[len] = code;
                _valOffset[len] = valIdx - code;

                for (int i = 0; i < count; i++)
                {
                    int curCode = code + i;
                    byte symbol = Values[valIdx++];

                    // Populate fast 8-bit lookup
                    if (len <= 8)
                    {
                        int shift = 8 - len;
                        int start = curCode << shift;
                        int end = start + (1 << shift);
                        for (int k = start; k < end; k++)
                        {
                            _lookup[k] = (short)((symbol << 8) | len);
                        }
                    }
                }

                code += count;
                _maxCode[len] = code - 1;
            }
            else
            {
                _maxCode[len] = -1;
            }

            code <<= 1;
        }
    }

    public int Decode(JpegBitReader reader)
    {
        // Try fast 8-bit lookup
        int peek = reader.PeekBits(8);
        short fast = _lookup[peek];
        if (fast >= 0)
        {
            int len = fast & 0xFF;
            reader.ConsumeBits(len);
            return fast >> 8;
        }

        // Slow multi-bit traversal
        int code = reader.ReadBit();
        for (int len = 1; len <= 16; len++)
        {
            if (code <= _maxCode[len] && _maxCode[len] >= 0)
            {
                int index = code + _valOffset[len];
                return Values[index];
            }
            code = (code << 1) | reader.ReadBit();
        }

        throw new FormatException("Corrupt JPEG Huffman code.");
    }
}

/// <summary>
/// Bit-level stream reader for JPEG entropy data, handling 0xFF 0x00 byte stuffing.
/// </summary>
public sealed class JpegBitReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private int _offset;
    private uint _bitBuffer;
    private int _bitsCount;

    public JpegBitReader(ReadOnlyMemory<byte> data, int startOffset)
    {
        _data = data;
        _offset = startOffset;
        _bitBuffer = 0;
        _bitsCount = 0;
    }

    public int ReadBit()
    {
        EnsureBits(1);
        int bit = (int)((_bitBuffer >> (_bitsCount - 1)) & 1);
        _bitsCount--;
        return bit;
    }

    public int PeekBits(int count)
    {
        EnsureBits(count);
        return (int)((_bitBuffer >> (_bitsCount - count)) & ((1u << count) - 1));
    }

    public void ConsumeBits(int count)
    {
        _bitsCount -= count;
    }

    public int ReadBits(int count)
    {
        if (count == 0) return 0;
        EnsureBits(count);
        int val = (int)((_bitBuffer >> (_bitsCount - count)) & ((1u << count) - 1));
        _bitsCount -= count;
        return val;
    }

    public int ReadSignedBits(int count)
    {
        int val = ReadBits(count);
        // Extend sign bit for JPEG DCT coefficients
        if (val < (1 << (count - 1)))
        {
            val += (-1 << count) + 1;
        }
        return val;
    }

    private void EnsureBits(int count)
    {
        while (_bitsCount < count)
        {
            byte b = ReadByteWithByteStuffing();
            _bitBuffer = (_bitBuffer << 8) | b;
            _bitsCount += 8;
        }
    }

    private byte ReadByteWithByteStuffing()
    {
        if (_offset >= _data.Length) return 0xFF; // Pad on EOF

        byte b = _data.Span[_offset++];
        if (b == 0xFF && _offset < _data.Length)
        {
            byte next = _data.Span[_offset];
            if (next == 0x00)
            {
                _offset++; // Skip 0x00 byte stuff
            }
        }
        return b;
    }
}
