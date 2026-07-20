using libCommon;
using libCommon.Streams;
using System;
using System.IO;
using GzipSeekable;

namespace libClonezilla.Decompressors
{
    /// <summary>Emits the gzip checkpoint-index build progress in the house log format, every ~1 GB of
    /// output. Implements IProgress directly (not Progress&lt;T&gt;) so reports stay synchronous.</summary>
    public class GzipBuildProgressLogger : IProgress<GzipIndexProgress>
    {
        const long IntervalBytes = 1L * 1024 * 1024 * 1024;
        long nextAt = IntervalBytes;

        public void Report(GzipIndexProgress value)
        {
            if (value.UncompressedBytesProduced < nextAt) return;
            var percentThroughCompressedSource = value.CompressedTotalBytes > 0
                ? 100.0 * value.CompressedBytesProcessed / value.CompressedTotalBytes
                : 0;
            Serilog.Log.Information($"Indexed {value.UncompressedBytesProduced.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");
            nextAt = (value.UncompressedBytesProduced / IntervalBytes + 1) * IntervalBytes;
        }
    }

    /// <summary>
    /// Bridges a GzipSeekable <c>GzipIndexedStream</c> into this codebase: implements
    /// <see cref="IReadSuggestor"/> as 32 MB-aligned sub-spans within the containing point span
    /// (with gzip's 4 MB default spans the sub-span is simply the whole span), so the CachingStream
    /// layer reads pooled-buffer-sized segments. The <paramref name="spanOf"/> delegate returns the
    /// containing span for a position. Mirrors <see cref="SeekableXzStream"/>.
    /// </summary>
    public class SeekableGzipStream(Stream inner, Func<long, (long Start, long End)> spanOf) : Stream, IReadSuggestor
    {
        const long RecommendationSubSpanBytes = 32L * 1024 * 1024;

        public (long Start, long End) GetRecommendation(long start)
        {
            if (start >= inner.Length) return (start, start);

            var (spanStart, spanEnd) = spanOf(start);
            var subSpanIndex = (start - spanStart) / RecommendationSubSpanBytes;
            var subSpanStart = spanStart + subSpanIndex * RecommendationSubSpanBytes;
            var subSpanEnd = Math.Min(subSpanStart + RecommendationSubSpanBytes, spanEnd);
            return (subSpanStart, subSpanEnd);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            //CachingStream issues ONE Read per recommendation and requires it to cover the requested
            //range; the inner stream short-reads at point/fill boundaries, so loop.
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
