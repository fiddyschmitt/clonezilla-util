using libClonezilla.Cache;
using libCommon;
using libCommon.Streams.Seekable;
using libZstd;
using Serilog;
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
                //in-memory random access via an index of verified resume points (window snapshots),
                //instead of extracting the whole stream to an on-disk cache. Returns null (falling
                //through to the extraction path) if this particular stream can't be reliably indexed.
                var indexFilename = PartitionCache.GetZstdIndexFilename();
                var seekable = ZstdStreamSeekable.TryCreate(CompressedStream, indexFilename);
                if (seekable != null) return seekable;
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
        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var uncompressedStream = new DecompressionStream(CompressedStream);
            return uncompressedStream;
        }
    }
}
