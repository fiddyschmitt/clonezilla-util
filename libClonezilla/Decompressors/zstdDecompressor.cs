using libClonezilla.Cache;
using libCommon;
using libCommon.Streams.Seekable;
using libCommon.Streams;
using Serilog;
using ZstdSeekable;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZstdNet;

namespace libClonezilla.Decompressors
{
    public class ZstdDecompressor : Decompressor
    {
        public IPartitionCache? PartitionCache { get; }

        public ZstdDecompressor(Stream compressedStream, IPartitionCache? partitionCache) : base(compressedStream)
        {
            PartitionCache = partitionCache;
        }

        public override Stream? GetSeekableStream()
        {
            if (PartitionCache != null)
            {
                //in-memory random access via the ZstdSeekable package: an index of verified resume
                //points (window snapshots) instead of extracting the whole stream to an on-disk
                //cache. Returns null (falling through to the sequential, restart-based path) if this particular
                //stream can't be indexed usefully.
                var indexFilename = PartitionCache.GetZstdIndexFilename();
                try
                {
                    if (!File.Exists(indexFilename))
                    {
                        Log.Information($"Creating zstd random-access index: {Path.GetFileName(indexFilename)}");
                    }

                    var options = new ZstdIndexOptions { Logger = new SerilogLoggerBridge() };
                    var index = ZstdIndex.LoadOrBuild(CompressedStream, indexFilename, options, new ZstdBuildProgressLogger());

                    //serving-quality gate: on a pathological stream the package degrades to
                    //frame-start-only points - always CORRECT, but for a huge single-frame stream
                    //that means decode-from-the-start seeks, which serves a mount far worse than
                    //the sequential fallback does. Only serve from an index with usable density.
                    var maxGap = MaxPointGap(index);
                    if (maxGap > 4 * options.TargetSpanBytes)
                    {
                        Log.Warning($"zstd index for {Path.GetFileName(indexFilename)} is too sparse to serve efficiently (largest resume gap {maxGap.BytesToString()}). Falling back to sequential decoding.");
                        index.Dispose();
                        return null;
                    }

                    var indexedStream = new ZstdIndexedStream(CompressedStream, index, leaveOpen: true);
                    return new SeekableZstdStream(indexedStream, index);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not build a zstd random-access index ({ex.Message}). Falling back to sequential decoding.");
                }
            }

            return null;
        }

        static long MaxPointGap(ZstdIndex index)
        {
            var maxGap = 0L;
            for (var i = 0; i < index.Points.Count; i++)
            {
                var end = i + 1 < index.Points.Count ? index.Points[i + 1].UncompressedOffset : index.UncompressedLength;
                maxGap = Math.Max(maxGap, end - index.Points[i].UncompressedOffset);
            }
            return maxGap;
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var uncompressedStream = new DecompressionStream(new NonClosingStream(CompressedStream));
            return uncompressedStream;
        }
    }
}
