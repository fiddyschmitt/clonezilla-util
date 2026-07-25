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
            //There used to be a 10-second probe here choosing between index-backed serving and a
            //sequential decoder behind restart-based seeking, from the era when building an index
            //meant a gztool subprocess or a full extraction. Every mainstream format now has a
            //cached in-process index, and the probe measured SEQUENTIAL throughput - so it chose
            //"sequential" precisely for small-compressed/huge-decompressed images, whose scattered
            //mount-time reads then crawled through restarts (TEST_ANALYSIS.md #21/#35, Lead L8).
            //Serving is now always index-backed where an index exists; formats without one fall
            //back to restarts below, exactly as the "sequential" verdict used to behave.
            Log.Information($"{StreamName} Using a seekable decompressor for this data.");

            bool addCacheLayer = true;
            Stream uncompressedStream;

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

                uncompressedStream = CreateRestartsStream();

                addCacheLayer = true;
            }
            else
            {
                uncompressedStream = seekableStream;

                if (uncompressedStream is FileStream)
                {
                    //raw uncompressed local file: serve it directly, no cache layer needed
                    addCacheLayer = false;
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

        string? wholeFileCacheFolder;

        /// <summary>
        /// Identity folder for a whole (unnamed) stream (key math in WholeFileCacheManager); the
        /// index files and uncompressed lengths synthesized for cache-less flows land in this folder,
        /// and containers built on this stream (drive images) use it to give their partitions real
        /// caches. Memoized: computing it decompresses 50 MB, and it is needed more than once per
        /// open (serving decision + synthesized index cache + container).
        /// </summary>
        public string GetWholeFileCacheFolder()
        {
            if (wholeFileCacheFolder != null)
            {
                return wholeFileCacheFolder;
            }

            var streamForHashing = Decompressor.GetSequentialStream();
            wholeFileCacheFolder = WholeFileCacheManager.GetCacheFolder(streamForHashing, StreamName, CompressedStream.Length);

            //hashing consumed part of the compressed stream; downstream consumers expect to
            //start at 0
            CompressedStream.Seek(0, SeekOrigin.Begin);

            return wholeFileCacheFolder;
        }

        public override Stream GetSequentialStream()
        {
            //Still have to make it seekable though
            return CreateRestartsStream();
        }

        /// <summary>Restart-based seekable stream over the sequential decompressor. When no length
        /// was provided, a length persisted by an earlier open is used - discovering it otherwise
        /// requires decoding the ENTIRE stream to EOF, on every open (24 s for a 33 GB near-empty
        /// zstd image; TEST_ANALYSIS.md #21, Lead L7). First discovery persists it.</summary>
        SeekableStreamUsingRestarts CreateRestartsStream()
        {
            var resolvedLength = UncompressedLength ?? ReadCachedUncompressedLength();

            var result = new SeekableStreamUsingRestarts(() =>
            {
                Log.Debug($"{StreamName} Creating new seekable stream.");
                var sequentialStream = Decompressor.GetSequentialStream();
                return sequentialStream;
            }, resolvedLength);

            if (resolvedLength == null)
            {
                result.OnLengthDiscovered = WriteCachedUncompressedLength;
            }

            return result;
        }

        /// <summary>Where this stream's persisted uncompressed length lives; same placement rules
        /// as the serving decision. Null when no cache location is resolvable.</summary>
        string? GetUncompressedLengthFilename()
        {
            try
            {
                if (PartitionCache != null)
                {
                    return PartitionCache.GetUncompressedLengthFilename();
                }
                return Path.Combine(GetWholeFileCacheFolder(), "uncompressed_length.txt");
            }
            catch (Exception ex)
            {
                Log.Debug($"{StreamName} No uncompressed-length cache available ({ex.Message}).");
                return null;
            }
        }

        long? ReadCachedUncompressedLength()
        {
            var filename = GetUncompressedLengthFilename();
            if (filename == null || !File.Exists(filename))
            {
                return null;
            }
            try
            {
                if (long.TryParse(File.ReadAllText(filename).Trim(), out var result) && result >= 0)
                {
                    Log.Information($"{StreamName} Using cached uncompressed length: {result:N0} bytes.");
                    return result;
                }
            }
            catch { }
            return null;    //unreadable or malformed: rediscover (and rewrite) it
        }

        void WriteCachedUncompressedLength(long value)
        {
            var filename = GetUncompressedLengthFilename();
            if (filename == null)
            {
                return;
            }
            try
            {
                File.WriteAllText(filename, value.ToString());
            }
            catch (Exception ex)
            {
                Log.Debug($"Non-fatal: could not persist the uncompressed length to {filename} ({ex.Message}).");
            }
        }
    }
}
