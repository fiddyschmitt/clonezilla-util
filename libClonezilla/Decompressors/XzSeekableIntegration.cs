using libCommon.Streams;
using System;
using System.IO;
using XzSeekable;

namespace libClonezilla.Decompressors
{
    /// <summary>
    /// Bridges the XzSeekable package's <see cref="XzBlockIndexedStream"/> (multi-block xz served via
    /// the native block index) into this codebase: implements <see cref="IReadSuggestor"/> as
    /// 32 MB-aligned sub-spans within a block, so the CachingStream layer reads pooled-buffer-sized
    /// segments instead of ~1 MB defaults (a cold ~1 MB read would otherwise decode-and-discard up to
    /// a whole block). Mirrors <see cref="SeekableZstdStream"/>.
    /// </summary>
    public class SeekableXzStream(XzBlockIndexedStream inner) : Stream, IReadSuggestor
    {
        const long RecommendationSubSpanBytes = 32L * 1024 * 1024;

        public (long Start, long End) GetRecommendation(long start)
        {
            if (start >= inner.Length) return (start, start);

            //the whole containing block, then a 32 MB-aligned sub-span within it (blocks are the
            //resume unit; a sub-span still decodes from the block start, but keeps cache segments
            //poolable - the same trade-off SeekableZstdStream makes within a point span)
            var (blockStart, blockEnd) = inner.GetRecommendation(start);
            var subSpanIndex = (start - blockStart) / RecommendationSubSpanBytes;
            var subSpanStart = blockStart + subSpanIndex * RecommendationSubSpanBytes;
            var subSpanEnd = Math.Min(subSpanStart + RecommendationSubSpanBytes, blockEnd);
            return (subSpanStart, subSpanEnd);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            //CachingStream issues ONE Read per recommendation and requires it to cover the requested
            //range; XzBlockIndexedStream short-reads at block boundaries, so loop.
            var total = 0;
            while (total < count)
            {
                var n = inner.Read(buffer, offset + total, count - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
