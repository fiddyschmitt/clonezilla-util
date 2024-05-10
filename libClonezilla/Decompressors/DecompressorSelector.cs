using libClonezilla.Cache;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libCommon.Streams.Sparse;
using libPartclone;
using libTrainCompress;
using libTrainCompress.Compressors;
using Newtonsoft.Json.Linq;
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
                Compression.xz => new xzDecompressor(CompressedStream),
                Compression.Zstandard => new ZstdDecompressor(CompressedStream),
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
                        bytesRead = testStream.CopyTo(Stream.Null, Buffers.ARBITARY_MEDIUM_SIZE_BUFFER, Buffers.ARBITARY_SMALL_SIZE_BUFFER);
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

                var seekableStream = Decompressor.GetSeekableStream();

                if (seekableStream == null)
                {
                    uncompressedStream = Stream.Null;
                    Log.Information($"{StreamName} uses {CompressionInUse} compression, which is not seekable. Extracting first.");

                    //we have to come up with a unique string to represent this stream, without reading the whole stream.
                    var streamForHashing = Decompressor.GetSequentialStream();
                    var beginningOfFile = new byte[50 * 1024 * 1024];
                    streamForHashing.Read(beginningOfFile);
                    var md5 = libCommon.Utility.CalculateMD5(beginningOfFile);
                    md5 = libCommon.Utility.CalculateMD5(Encoding.UTF8.GetBytes($"{md5} {StreamName} {CompressedStream.Length}"));
                    var cacheFolder = Path.Combine(WholeFileCacheManager.RootCacheFolder, md5);
                    Directory.CreateDirectory(cacheFolder);

                    var cachedFilename = Path.Combine(cacheFolder, "cache.train");

                    var compressors = new List<libTrainCompress.Compressors.Compressor>()
                    {
                        new zstdCompressor()
                    };

                    //check if we've already cached the file
                    if (File.Exists(cachedFilename))
                    {
                        Log.Information($"Using cached file: {cachedFilename}");
                    }
                    else
                    {
                        //if (Utility.ConsoleConfirm("Would you like to extract it now?"))
                        if (true)
                        {
                            Log.Information($"Creating cache file: {cachedFilename}");

                            var decompressedStream = Decompressor.GetSequentialStream();

                            var wipFilename = Path.ChangeExtension(cachedFilename, ".wip");
                            if (File.Exists(wipFilename))
                            {
                                File.Delete(wipFilename);
                            }

                            /*
                            using (var wipStream = File.Create(wipFilename))
                            {
                                using var sparseAwareReader = new SparseAwareReader(decompressedStream);
                                using var trainCompressor = new TrainCompressor(wipStream, compressors, 10 * 1024 * 1024);

                                var totalUncompressedBytes = 0L;
                                var sequenceOfEmptyBytes = 0L;

                                while (true)
                                {
                                    var read = sparseAwareReader.CopyTo(trainCompressor, Buffers.ARBITARY_LARGE_SIZE_BUFFER, Buffers.ARBITARY_MEDIUM_SIZE_BUFFER);
                                    if (read == 0)
                                    {
                                        break;
                                    }

                                    totalUncompressedBytes += read;

                                    var percentThroughCompressedSource = (double)CompressedStream.Position / CompressedStream.Length * 100;
                                    Log.Information($"{StreamName} Cached {totalUncompressedBytes.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");

                                    //disabled for now, because it's hard to know where the trailing nulls start
                                    if (!ProcessTrailingNulls && percentThroughCompressedSource > 90 && false)
                                    {
                                        if (sparseAwareReader.LatestReadWasAllNull)
                                        {
                                            sequenceOfEmptyBytes += read;
                                        }
                                        else
                                        {
                                            sequenceOfEmptyBytes = 0;
                                        }

                                        if (sequenceOfEmptyBytes > 4L * 1024 * 1024 * 1024)
                                        {
                                            Log.Warning($"{StreamName} The end of this stream has more than 4GB of empty bytes. Assuming the rest of the file is empty.");
                                            break;
                                        }
                                    }
                                }
                            }
                            */

                            using (var wipStream = File.Create(wipFilename))
                            {
                                using var trainCompressor = new TrainCompressor(wipStream, compressors, 10 * 1024 * 1024);
                                decompressedStream.CopyTo(trainCompressor, Buffers.ARBITARY_LARGE_SIZE_BUFFER, progress =>
                                {
                                    try
                                    {
                                        var perThroughCompressedSource = (double)CompressedStream.Position / CompressedStream.Length * 100;

                                        Log.Information($"{StreamName} Cached {progress.TotalRead.BytesToString()}. ({perThroughCompressedSource:N0}% through source file)");
                                    }
                                    catch
                                    {
                                        //just in case the Close() call below causes the percentage calculation to fail
                                    }
                                });
                            }

                            File.Move(wipFilename, cachedFilename);

                            Log.Information($"Successfully cached to: {cachedFilename}");

                            var metadataJson = JObject.FromObject(new
                            {
                                CacheInfo = new
                                {
                                    OriginalLocation = OriginFilename,
                                    StreamName
                                }
                            }).ToString();

                            var metadataFilename = Path.Combine(WholeFileCacheManager.RootCacheFolder, md5, "metadata.json");
                            File.WriteAllText(metadataFilename, metadataJson);
                        }
                        else
                        {
                            /*
                            Console.WriteLine("Exiting...");
                            Environment.Exit(1);
                            */
                        }
                    }

                    var cachedTrain = File.OpenRead(cachedFilename);
                    uncompressedStream = new TrainDecompressor(cachedTrain, compressors);

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
