using System.Buffers;

namespace Glacier.Graphics.Pdf;

public partial class TinyPdfCreate
{
    // Pooled growable buffer writer that exposes written chunks so we can stream compressed output
    internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int DefaultBlockSize = 8192;
        private readonly List<byte[]> _blocks = new List<byte[]>();
        private readonly List<int> _blockLengths = new List<int>();
        private int _posInLast = 0;
        private int _written = 0;

        public int WrittenCount => _written;

        public void Advance(int count)
        {
            _posInLast += count;
            _written += count;
        }

        private void EnsureCapacity(int sizeHint)
        {
            int needed = Math.Max(1, sizeHint);
            if (_blocks.Count == 0 || (_blocks[_blocks.Count - 1].Length - _posInLast < needed))
            {
                if (_blocks.Count > 0)
                {
                    _blockLengths[_blocks.Count - 1] = _posInLast;
                }
                int newSize = Math.Max(DefaultBlockSize, needed);
                var buf = ArrayPool<byte>.Shared.Rent(newSize);
                _blocks.Add(buf);
                _blockLengths.Add(0);
                _posInLast = 0;
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            var last = _blocks[_blocks.Count - 1];
            return new Memory<byte>(last, _posInLast, last.Length - _posInLast);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            var last = _blocks[_blocks.Count - 1];
            return new Span<byte>(last, _posInLast, last.Length - _posInLast);
        }

        // Write all stored chunks into destination stream
        public void CopyTo(Stream dest)
        {
            for (int i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                int count = (i == _blocks.Count - 1) ? _posInLast : _blockLengths[i];
                if (count > 0) dest.Write(block, 0, count);
            }
        }

        public void Dispose()
        {
            foreach (var b in _blocks)
            {
                ArrayPool<byte>.Shared.Return(b);
            }
            _blocks.Clear();
            _blockLengths.Clear();
            _posInLast = 0;
            _written = 0;
        }
    }
}
