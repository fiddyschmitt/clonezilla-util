using libClonezilla.Cache;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libGZip;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZstdNet;

namespace libClonezilla.Decompressors
{
    public class DecompressorSelector : IDecompressor
    {
        public DecompressorSelector(string streamName, Stream compressedStream, long? uncompressedLength, Compression compressionInUse, IPartitionCache? partitionCache)
        {
            StreamName = streamName;
            CompressedStream = compressedStream;
            UncompressedLength = uncompressedLength;
            CompressionInUse = compressionInUse;
            PartitionCache = partitionCache;

            Decompressor = CompressionInUse switch
            {
                Compression.Gzip => new GzDecompressor(
                                        CompressedStream,
                                        uncompressedLength,
                                        partitionCache ?? throw new Exception($"{nameof(GzDecompressor)} requires a partition cache. Not yet implemented.")),

                Compression.Zstandard => new ZstDecompressor(CompressedStream, uncompressedLength),
                Compression.xz => new xzDecompressor(CompressedStream),
                Compression.None => new NoChangeDecompressor(CompressedStream),
                _ => throw new Exception($"Could not initialise a decompressor for {StreamName}"),
            };
        }

        public string StreamName { get; }
        public Stream CompressedStream { get; }
        public long? UncompressedLength { get; }
        public Compression CompressionInUse { get; }
        public IPartitionCache? PartitionCache { get; }
        public IDecompressor Decompressor;

        public Stream GetSeekableStream()
        {
            //Do a performance test. If the entire file can be read quickly then let's not bother using any indexing

            var testDurationSeconds = 10;
            Log.Debug($"{StreamName} Running a {testDurationSeconds:N0} second performance test to determine the optimal way to serve it.");

            var startTime = DateTime.Now;
            var testStream = Decompressor.GetSequentialStream();
            {
                while (true)
                {
                    var bytesRead = testStream.CopyTo(Stream.Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER, Buffers.ARBITARY_LARGE_SIZE_BUFFER);
                    if (bytesRead == 0) break;
                    var duration = DateTime.Now - startTime;
                    if (duration.TotalSeconds > testDurationSeconds) break;
                }
            }

            var progressThroughFile = CompressedStream.Position / (double)CompressedStream.Length;  //the compressed stream is more accurate than the uncompressed stream, for determining how far we got through the stream
            var testDuration = DateTime.Now - startTime;
            Log.Debug($"Processed {CompressedStream.Position.BytesToString()} ({progressThroughFile * 100:N1}%) of the compressed data in {testDuration.TotalSeconds:N1} seconds.");
            var predictedSecondsToReadEntireFile = testDuration.TotalSeconds / progressThroughFile;
            Log.Debug($"At that rate, it would take {predictedSecondsToReadEntireFile:N1} seconds to read the entire file.");

            CompressedStream.Seek(0, SeekOrigin.Begin);

            bool addCacheLayer = true;
            Stream uncompressedStream;
            if (predictedSecondsToReadEntireFile < 10)
            {
                Log.Debug($"Using a sequential decompressor for this data.");

                if (testStream is FileStream)
                {
                    addCacheLayer = false;
                }

                //Still have to make it seekable though
                var seekableStreamUsingRestarts = new SeekableStreamUsingRestarts(() =>
                {
                    Log.Debug($"{StreamName} Creating new seekable stream.");
                    var sequentialStream = Decompressor.GetSequentialStream();
                    return sequentialStream;
                }, UncompressedLength);

                uncompressedStream = seekableStreamUsingRestarts;
            }
            else
            {
                Log.Debug($"Using a seekable decompressor for this data.");
                var seekableStream = Decompressor.GetSeekableStream();
                uncompressedStream = seekableStream;

                if (uncompressedStream is FileStream)
                {
                    addCacheLayer = false;
                }
            }

            if (addCacheLayer)
            {
                //add a cache layer
                var readSuggestor = uncompressedStream as IReadSuggestor;

                var totalSystemRAMInBytes = libCommon.Utility.GetTotalRamSizeBytes();
                var totalSystemRAMInMegabytes = (int)(totalSystemRAMInBytes / (double)(1024 * 1024));
                var maxCacheSizeInMegabytes = totalSystemRAMInMegabytes / 4;

                uncompressedStream = new CachingStream(uncompressedStream, readSuggestor, EnumCacheType.LimitByRAMUsage, maxCacheSizeInMegabytes, null);
            }

            return uncompressedStream;
        }

        public Stream GetSequentialStream()
        {
            //Still have to make it seekable though
            var seekableStreamUsingRestarts = new SeekableStreamUsingRestarts(() =>
            {
                var sequentialStream = Decompressor.GetSequentialStream();
                return sequentialStream;
            }, UncompressedLength);

            return seekableStreamUsingRestarts;
        }
    }
}
