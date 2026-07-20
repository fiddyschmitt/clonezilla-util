using GzipSeekable;
using libClonezilla.Cache;
using libCommon;
using Serilog;
using System;
using System.IO;
using System.IO.Compression;

namespace libClonezilla.Decompressors
{
    public class GzDecompressor(Stream compressedStream, IPartitionCache? partitionCache) : Decompressor(compressedStream)
    {
        public IPartitionCache? PartitionCache { get; } = partitionCache;

        public override Stream? GetSeekableStream()
        {
            if (PartitionCache == null)
            {
                return null;   //nowhere to persist an index -> restart-based fallback
            }

            try
            {
                var indexFilename = PartitionCache.GetGzipIndexFilename();
                if (!File.Exists(indexFilename))
                {
                    Log.Information($"Creating gzip random-access index: {Path.GetFileName(indexFilename)}");
                }

                var options = new GzipIndexOptions { Logger = new SerilogLoggerBridge() };
                CompressedStream.Seek(0, SeekOrigin.Begin);
                var index = GzipIndex.LoadOrBuild(CompressedStream, indexFilename, options, new GzipBuildProgressLogger());

                //serving-quality gate (mirrors xzDecompressor): if points are somehow too sparse to
                //serve efficiently, prefer the restart-based fallback. Deflate blocks are small and
                //member starts only add density, so this never fires on sane streams.
                var maxGap = MaxPointGap(index);
                if (maxGap > 4 * options.TargetSpanBytes)
                {
                    Log.Warning($"gzip index for {Path.GetFileName(indexFilename)} is too sparse to serve efficiently (largest gap {maxGap.BytesToString()}). Falling back.");
                    index.Dispose();
                    return null;
                }

                var stream = new GzipIndexedStream(CompressedStream, index, leaveOpen: true);
                return new SeekableGzipStream(stream, start =>
                {
                    var r = stream.GetRecommendation(start);
                    return (r.Start, r.End);
                });
            }
            catch (Exception ex)
            {
                Log.Warning($"gzip: could not build a random-access index ({ex.Message}). Falling back.");
                return null;
            }
        }

        static long MaxPointGap(GzipIndex index)
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
            var uncompressedStream = new GZipStream(CompressedStream, CompressionMode.Decompress);
            return uncompressedStream;
        }
    }
}
