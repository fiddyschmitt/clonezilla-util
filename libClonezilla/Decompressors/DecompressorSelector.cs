using libClonezilla.Cache;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libCommon.Streams.Sparse;
using libPartclone;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using ZstdNet;

namespace libClonezilla.Decompressors
{
    public class DecompressorSelector : Decompressor
    {
        public DecompressorSelector(
            string originFilename,
            string streamName,
            Stream compressedStream,
            long? uncompressedLength,
            Compression compressionInUse,
            IPartitionCache? partitionCache,
            bool processTrailingNulls) : base(compressedStream)
        {
            OriginFilename = originFilename;
            StreamName = streamName;
            UncompressedLength = uncompressedLength;
            CompressionInUse = compressionInUse;
            PartitionCache = partitionCache;
            ProcessTrailingNulls = processTrailingNulls;

            Decompressor = CompressionInUse switch
            {
                Compression.bzip2 => new Bzip2Decompressor(CompressedStream, partitionCache, processTrailingNulls),
                Compression.Gzip => new GzDecompressor(CompressedStream, partitionCache),
                Compression.LZ4 => new LZ4Decompressor(CompressedStream),
                Compression.LZip => new LZipDecompressor(CompressedStream),
                Compression.None => new NoChangeDecompressor(CompressedStream),
                Compression.xz => new xzDecompressor(CompressedStream, partitionCache),
                Compression.Zstandard => new ZstdDecompressor(CompressedStream, partitionCache),
                _ => throw new Exception($"Could not initialise a decompressor for {StreamName}"),
            };
        }

        public string OriginFilename { get; }
        public string StreamName { get; }
        public long? UncompressedLength { get; }
        public Compression CompressionInUse { get; }
        public IPartitionCache? PartitionCache { get; }
        public bool ProcessTrailingNulls { get; }

        public Decompressor Decompressor;

        public override Stream GetSeekableStream()
        {
            //Do a performance test. If the entire file can be read quickly then let's not bother using any indexing

            var testDurationSeconds = 10;
            Log.Information($"{StreamName} Running a {testDurationSeconds:N0} second performance test to determine the optimal way to serve it.");

            var startTime = DateTime.Now;
            var testStream = Decompressor.GetSequentialStream();
            {
                while (true)
                {
                    var bytesRead = 0L;

                    try
                    {
                        bytesRead = testStream.CopyTo(Stream.Null, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER, Buffers.ARBITRARY_SMALL_SIZE_BUFFER);
                    }
                    catch { }
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
                Log.Information($"{StreamName} Using a sequential decompressor for this data.");

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
                Log.Information($"{StreamName} Using a seekable decompressor for this data.");

                //gz, zstd, bzip2 and (single-block) xz have in-memory random-access support, but
                //need somewhere to keep their index file. Flows that serve whole (drive) images
                //provide no partition cache, so synthesize one rooted in the whole-file cache
                //folder - this is what lets drive images use the indexes instead of degrading to
                //restart-based seeking. (Multi-block xz needs no cache - its index is native.)
                if (PartitionCache == null && CompressionInUse is Compression.Gzip or Compression.Zstandard or Compression.bzip2 or Compression.xz)
                {
                    var synthesizedCache = new PartitionCache(GetWholeFileCacheFolder(), StreamName);
                    Decompressor = CompressionInUse switch
                    {
                        Compression.Gzip => new GzDecompressor(CompressedStream, synthesizedCache),
                        Compression.Zstandard => new ZstdDecompressor(CompressedStream, synthesizedCache),
                        Compression.bzip2 => new Bzip2Decompressor(CompressedStream, synthesizedCache, ProcessTrailingNulls),
                        Compression.xz => new xzDecompressor(CompressedStream, synthesizedCache),
                        _ => Decompressor,
                    };
                }

                var seekableStream = Decompressor.GetSeekableStream();

                if (seekableStream == null)
                {
                    //No random-access index exists for this stream: large lz4/lzip (no index support
                    //yet), a single-block xz drive image with nowhere to keep an index, or an index
                    //build failure. Serve by re-decoding from the start on backward seeks - correct
                    //and fully in-memory, though slow for large images. (This replaced the old
                    //cache.train extraction, which materialised the entire decompressed image to
                    //disk; every mainstream format - gz, bzip2, zstd, xz - now has a real index, so
                    //the extraction subsystem and libTrainCompress are gone.)
                    Log.Warning($"{StreamName} uses {CompressionInUse} compression, which has no random-access index. Serving via restart-based seeking; this can be slow for large images.");

                    uncompressedStream = new SeekableStreamUsingRestarts(() =>
                    {
                        var sequentialStream = Decompressor.GetSequentialStream();
                        return sequentialStream;
                    }, UncompressedLength);

                    addCacheLayer = true;
                }
                else
                {
                    Log.Debug($"Using a seekable decompressor for this data.");

                    uncompressedStream = seekableStream;

                    if (uncompressedStream is FileStream)
                    {
                        addCacheLayer = false;
                    }
                }
            }

            //addCacheLayer = false;
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

        /// <summary>
        /// Identity folder for a whole (unnamed) stream without reading all of it: MD5 of the first
        /// 50 MB of DECOMPRESSED content, salted with the stream name and compressed length. This is
        /// the exact key the old extraction cache used, so existing index-cache folders stay valid;
        /// the gz/zstd index files synthesized for cache-less flows land in the same folder.
        /// </summary>
        string GetWholeFileCacheFolder()
        {
            var streamForHashing = Decompressor.GetSequentialStream();
            var beginningOfFile = new byte[50 * 1024 * 1024];
            //a single Read() can return fewer bytes than requested (and how many is not deterministic), which would make the cache key vary from run to run
            streamForHashing.ReadAtLeast(beginningOfFile, beginningOfFile.Length, throwOnEndOfStream: false);
            var md5 = libCommon.Utility.CalculateMD5(beginningOfFile);
            md5 = libCommon.Utility.CalculateMD5(Encoding.UTF8.GetBytes($"{md5} {StreamName} {CompressedStream.Length}"));
            var cacheFolder = Path.Combine(WholeFileCacheManager.RootCacheFolder, md5);
            Directory.CreateDirectory(cacheFolder);

            //hashing consumed part of the compressed stream; downstream consumers (e.g. the gzip
            //index build, which pipes the stream to gztool from its CURRENT position) must start at 0
            CompressedStream.Seek(0, SeekOrigin.Begin);

            return cacheFolder;
        }

        public override Stream GetSequentialStream()
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
