using libCommon;
using libCommon.Streams;
using libDecompression;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace libZstd
{
    /// <summary>
    /// Random-access view over a standard zstd stream, backed by a <see cref="ZstdSeekableIndex"/> of
    /// verified resume points (see that class for why points must be verified). Gives zstd the same
    /// in-memory seekable treatment gz (gztool index) and bzip2 (block index) already have, instead
    /// of extracting the whole stream to an on-disk cache.
    /// </summary>
    public class ZstdStreamSeekable : SeekableDecompressingStream, IReadSuggestor
    {
        //CachingStream reads whole recommendations; recommending full ~64 MB spans would defeat the
        //pooled segment buffers (Buffers.BufferPool caps at ~50 MB). Recommend 32 MB-aligned sub-spans
        //instead: sequential consumers cost ~1.5x decode per span (resume + skip), random consumers
        //only decode up to their sub-span.
        const long RecommendationSubSpanBytes = 32L * 1024 * 1024;

        readonly ZstdSeekableIndex index;
        readonly List<Mapping> mappings;
        readonly SharedStream sharedSource;

        public Stream CompressedStream { get; }

        sealed class ZstdMapping : Mapping
        {
            public required ZstdIndexPoint Point;
        }

        ZstdStreamSeekable(Stream compressedStream, ZstdSeekableIndex index)
        {
            CompressedStream = compressedStream;
            sharedSource = new SharedStream(compressedStream);
            this.index = index;

            mappings = new List<Mapping>(index.Points.Count);
            for (var i = 0; i < index.Points.Count; i++)
            {
                var point = index.Points[i];
                var next = i + 1 < index.Points.Count ? index.Points[i + 1] : null;
                mappings.Add(new ZstdMapping
                {
                    Point = point,
                    CompressedStartByte = point.CompressedOffset,
                    CompressedEndByte = next?.CompressedOffset ?? compressedStream.Length,
                    UncompressedStartByte = point.UncompressedOffset,
                    UncompressedEndByte = next?.UncompressedOffset ?? index.UncompressedTotalLength,
                });
            }
        }

        ZstdStreamSeekable(ZstdStreamSeekable parent)
        {
            CompressedStream = parent.CompressedStream;
            sharedSource = parent.sharedSource;
            index = parent.index;
            mappings = parent.mappings;
        }

        /// <summary>An independent cursor over the same source and index: its own position, sharing
        /// the (internally locked) base stream. Views may be read concurrently with this instance
        /// and each other.</summary>
        public ZstdStreamSeekable CreateView() => new(this);

        /// <summary>Loads or builds the index; returns null (no seekable stream) if the stream cannot
        /// be reliably indexed - the caller then falls back to extraction.</summary>
        public static ZstdStreamSeekable? TryCreate(Stream compressedStream, string indexFilename)
        {
            var index = ZstdSeekableIndex.LoadOrCreate(compressedStream, indexFilename);
            if (index == null) return null;
            return new ZstdStreamSeekable(compressedStream, index);
        }

        public override IList<Mapping> Blocks => mappings;

        public override long UncompressedTotalLength => index.UncompressedTotalLength;

        public override int ReadFromChunk(Mapping chunk, byte[] buffer, int offset, int count)
        {
            var zstdChunk = (ZstdMapping)chunk;

            //never serve bytes beyond this chunk from this chunk's resume: only output WITHIN the
            //verified span is guaranteed (decoder state at the span's end is not). A short read makes
            //the caller re-enter at the next chunk.
            var bytesLeftInChunk = chunk.UncompressedEndByte - Position;
            var bytesLeftInFile = Length - Position;
            count = (int)Math.Min(count, Math.Min(bytesLeftInChunk, bytesLeftInFile));
            if (count <= 0) return 0;

            var positionInChunk = Position - chunk.UncompressedStartByte;

            var window = index.LoadWindow(zstdChunk.Point);
            var source = sharedSource.CreateView();
            using var resume = new ZstdResumeStream(source, chunk.CompressedStartByte, chunk.CompressedEndByte,
                                                    zstdChunk.Point.IsFrameStart, zstdChunk.Point.WindowDescriptor, window);

            if (positionInChunk > 0)
            {
                resume.CopyTo(Stream.Null, positionInChunk, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
            }

            var total = 0;
            while (total < count)
            {
                var n = resume.Read(buffer, offset + total, count - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        //Re-implemented (the base's recommendation is the whole block): a 32 MB-aligned sub-span
        //within the block, so the CachingStream layer above stays within pooled-buffer sizes.
        public new (long Start, long End) GetRecommendation(long start)
        {
            var block = Blocks.BinarySearch(start, MappingComparer) ?? throw new Exception($"Could not find block which contains position {start:N0}");

            var offsetInBlock = start - block.UncompressedStartByte;
            var subSpanIndex = offsetInBlock / RecommendationSubSpanBytes;
            var subSpanStart = block.UncompressedStartByte + subSpanIndex * RecommendationSubSpanBytes;
            var subSpanEnd = Math.Min(subSpanStart + RecommendationSubSpanBytes, block.UncompressedEndByte);
            return (subSpanStart, subSpanEnd);
        }
    }
}
