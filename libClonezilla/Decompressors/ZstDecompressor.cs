using libCommon;
using libCommon.Streams.Seekable;
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
    public class ZstDecompressor : IDecompressor
    {
        public ZstDecompressor(Stream compressedStream, long? uncompressedLength)
        {
            CompressedStream = compressedStream;
            UncompressedLength1 = uncompressedLength;
        }

        public Stream CompressedStream { get; }
        public long? UncompressedLength1 { get; }
        public long UncompressedLength { get; }

        public Stream GetSeekableStream()
        {
            //For now, let's extract it to a file so that we can have fast seeking

            Log.Information($"Zstandard doesn't support random seeking. Extracting to a temporary file.");

            var decompressor = new DecompressionStream(CompressedStream);

            var tempFilename = TempUtility.GetTempFilename(true);
            var tempFileStream = new FileStream(tempFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);

            decompressor.CopyTo(tempFileStream, Buffers.ARBITARY_HUGE_SIZE_BUFFER,
                totalCopied =>
                {
                    var totalCopiedStr = libCommon.Extensions.BytesToString(totalCopied);
                    Log.Information($"Extracted {totalCopiedStr} of Zstandard-compressed file to {tempFilename}.");
                });

            return tempFileStream;


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

        public Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var uncompressedStream = new DecompressionStream(CompressedStream);
            return uncompressedStream;
        }
    }
}
