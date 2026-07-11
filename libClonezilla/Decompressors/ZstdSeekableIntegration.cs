using libCommon;
using libCommon.Streams;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using ZstdSeekable;

namespace libClonezilla.Decompressors
{
    /// <summary>
    /// Bridges the ZstdSeekable package's <see cref="ZstdIndexedStream"/> into this codebase:
    /// implements <see cref="IReadSuggestor"/> (32 MB-aligned sub-spans within a resume point's
    /// span) so the CachingStream layer reads pooled-buffer-sized segments. Without a suggestor,
    /// CachingStream falls back to ~1 MB segments and every cold segment would pay a resume plus
    /// up-to-a-span of decode-and-discard - a 30-60x decode amplification on cold scans.
    /// </summary>
    public class SeekableZstdStream(ZstdIndexedStream inner, ZstdIndex index) : Stream, IReadSuggestor
    {
        const long RecommendationSubSpanBytes = 32L * 1024 * 1024;

        public (long Start, long End) GetRecommendation(long start)
        {
            //at/past EOF there is nothing to recommend; an empty range makes CachingStream return 0
            //cleanly (the old in-repo implementation threw here)
            if (start >= index.UncompressedLength) return (start, start);

            var points = index.Points;

            //binary search for the containing point span
            int lo = 0, hi = points.Count - 1, containing = 0;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (points[mid].UncompressedOffset <= start) { containing = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            var spanStart = points[containing].UncompressedOffset;
            var spanEnd = containing + 1 < points.Count ? points[containing + 1].UncompressedOffset : index.UncompressedLength;

            var subSpanIndex = (start - spanStart) / RecommendationSubSpanBytes;
            var subSpanStart = spanStart + subSpanIndex * RecommendationSubSpanBytes;
            var subSpanEnd = Math.Min(subSpanStart + RecommendationSubSpanBytes, spanEnd);
            return (subSpanStart, subSpanEnd);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            //CachingStream issues ONE Read per recommendation and requires it to cover the requested
            //range. ZstdIndexedStream returns short at chunk (point-span) boundaries and at fill-span
            //edges (it stops decoding where a fill span begins so the next read is served without the
            //decoder), so a recommendation-sized read must loop across those transitions.
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
            if (disposing)
            {
                inner.Dispose();
                index.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Emits the package's index-build progress in the house log format, every ~1 GB of
    /// output. Implements IProgress directly (not Progress&lt;T&gt;, which marshals to a
    /// synchronisation context) so reports stay synchronous and ordered.</summary>
    public class ZstdBuildProgressLogger : IProgress<ZstdIndexProgress>
    {
        const long IntervalBytes = 1L * 1024 * 1024 * 1024;
        long nextAt = IntervalBytes;

        public void Report(ZstdIndexProgress value)
        {
            if (value.UncompressedBytesProduced < nextAt) return;
            var percentThroughCompressedSource = value.CompressedTotalBytes > 0
                ? 100.0 * value.CompressedBytesProcessed / value.CompressedTotalBytes
                : 0;
            Serilog.Log.Information($"Indexed {value.UncompressedBytesProduced.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");
            nextAt = (value.UncompressedBytesProduced / IntervalBytes + 1) * IntervalBytes;
        }
    }

    /// <summary>Forwards the package's Microsoft.Extensions.Logging diagnostics to Serilog.</summary>
    public class SerilogLoggerBridge : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            switch (logLevel)
            {
                case Microsoft.Extensions.Logging.LogLevel.Critical:
                case Microsoft.Extensions.Logging.LogLevel.Error:
                    Serilog.Log.Error(message);
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Warning:
                    Serilog.Log.Warning(message);
                    break;
                case Microsoft.Extensions.Logging.LogLevel.Information:
                    Serilog.Log.Information(message);
                    break;
                default:
                    Serilog.Log.Debug(message);
                    break;
            }
        }
    }
}
