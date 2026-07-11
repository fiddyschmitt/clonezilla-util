using libClonezilla.Cache;
using libCommon;
using libCommon.Streams.Seekable;
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
                //cache. Returns null (falling through to the extraction path) if this particular
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
                    //the extraction fallback does. Only serve from an index with usable density.
                    var maxGap = MaxPointGap(index);
                    if (maxGap > 4 * options.TargetSpanBytes)
                    {
                        Log.Warning($"zstd index for {Path.GetFileName(indexFilename)} is too sparse to serve efficiently (largest resume gap {maxGap.BytesToString()}). Extracting instead.");
                        index.Dispose();
                        return null;
                    }

                    var indexedStream = new ZstdIndexedStream(CompressedStream, index, leaveOpen: true);
                    return new SeekableZstdStream(indexedStream, index);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not build a zstd random-access index ({ex.Message}). Falling back to extraction.");
                }
            }

            return null;
            //For now, let's extract it to a file so that we can have fast seeking
            /*
            Log.Information($"Zstandard doesn't support random seeking. Extracting to a temporary file.");

            var decompressor = new DecompressionStream(CompressedStream);

            var tempFilename = TempUtility.GetTempFilename(true);
            var tempFileStream = new FileStream(tempFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);

            decompressor.CopyTo(tempFileStream, Buffers.ARBITRARY_HUGE_SIZE_BUFFER,
                totalCopied =>
                {
                    var totalCopiedStr = libCommon.Extensions.BytesToString(totalCopied);
                    Log.Information($"Extracted {totalCopiedStr} of Zstandard-compressed file to {tempFilename}.");
                });

            return tempFileStream;
            */


            //FPS 04/01/2021: An experiment to park partially processed streams all over the file. Unfortunately this was still too slow
            /*
            var zstdDecompressorGenerator = new Func<Stream>(() =>
            {
                var compressedStream = compressedStreamGenerator();
                var zstdDecompressorStream = new DecompressionStream(compressedStream);

                //the Zstandard decompressor doesn't track position in stream, so we have to do it for them
                var positionTrackerStream = new PositionTrackerStream(zstdDecompressorStream);

                return positionTrackerStream;
            });

            uncompressedStream = new SeekableStreamUsingNearestActioner(zstdDecompressorGenerator, totalLength, 1 * 1024 * 1024);   //stations should be within one second of an actioner.
            */
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
            var uncompressedStream = new DecompressionStream(CompressedStream);
            return uncompressedStream;
        }
    }
}
