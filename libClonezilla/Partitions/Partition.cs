using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.IO;
using libCommon.Streams;
using libPartclone;
using libGZip;
using libCommon;
using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
using Serilog;
using libCommon.Streams.Sparse;
using ZstdNet;
using libCommon.Streams.Seekable;
using libPartclone.Cache;

namespace libClonezilla.Partitions
{
    public class Partition
    {
        public PartcloneStream FullPartitionImage { get; }
        public string Name { get; }

        public Partition(Stream compressedPartcloneStream, Compression compressionInUse, string partitionName, IPartitionCache? partitionCache, IPartcloneCache? partcloneCache, bool willPerformRandomSeeking)
        {
            Log.Information($"[{partitionName}] Loading partition information");

            (Stream Stream, bool UseCacheLayer)? uncompressedPartcloneStream = null;

            if (willPerformRandomSeeking)
            {
                if (compressionInUse == Compression.None)
                {
                    uncompressedPartcloneStream = (compressedPartcloneStream, false);
                }
                else
                {
                    //Do a performance test. If the entire file can be read quickly then let's not bother using any indexing

                    var testDurationSeconds = 10;
                    Log.Debug($"[{partitionName}] Running a {testDurationSeconds:N0} second performance test to determine the optimal way to serve it.");

                    Stream testStream = compressionInUse switch
                    {
                        Compression.Gzip => new GZipStream(compressedPartcloneStream, CompressionMode.Decompress),
                        Compression.Zstandard => new DecompressionStream(compressedPartcloneStream),
                        _ => throw new Exception($"Did not initialize a stream for partition {partitionName}"),
                    };

                    var startTime = DateTime.Now;
                    while (true)
                    {
                        var bytesRead = testStream.CopyTo(Stream.Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER, Buffers.ARBITARY_LARGE_SIZE_BUFFER);
                        if (bytesRead == 0) break;
                        var duration = DateTime.Now - startTime;
                        if (duration.TotalSeconds > testDurationSeconds) break;
                    }

                    var progressThroughFile = compressedPartcloneStream.Position / (double)compressedPartcloneStream.Length;  //the compressed stream is more accurate than the uncompressed stream, for determining how far we got through the stream
                    var testDuration = DateTime.Now - startTime;
                    Log.Debug($"Processed {compressedPartcloneStream.Position.BytesToString()} ({progressThroughFile * 100:N1}%) of the compressed data in {testDuration.TotalSeconds:N1} seconds.");

                    var predictedSecondsToReadEntireFile = testDuration.TotalSeconds / progressThroughFile;
                    Log.Debug($"At that rate, it would take {predictedSecondsToReadEntireFile:N1} seconds to read the entire file.");

                    compressedPartcloneStream.Seek(0, SeekOrigin.Begin);

                    //predictedSecondsToReadEntireFile = int.MaxValue;

                    if (predictedSecondsToReadEntireFile < 10)
                    {
                        Log.Debug($"Using a standard decompressor ({testStream}) for this data.");

                        var seekableStreamUsingRestarts = new SeekableStreamUsingRestarts(() =>
                        {
                            compressedPartcloneStream.Seek(0, SeekOrigin.Begin);

                            Stream newStream = compressionInUse switch
                            {
                                Compression.Gzip => new GZipStream(compressedPartcloneStream, CompressionMode.Decompress),
                                Compression.Zstandard => new DecompressionStream(compressedPartcloneStream),
                                _ => throw new Exception($"Did not initialize a stream for partition {partitionName}"),
                            };

                            return newStream;
                        });

                        uncompressedPartcloneStream = (seekableStreamUsingRestarts, true);
                    }
                    else
                    {
                        Log.Debug($"Will need to use a specialised approach for this {compressionInUse} data.");
                    }
                }

                if (uncompressedPartcloneStream == null && compressionInUse == Compression.Gzip)
                {
                    //this is faster for random seeks (eg. serving the full image (or file contents) in a Virtual File System)
                    //Uses gztool to create an index for fast seek, plus a cache layer to avoid using gztool for small reads

                    if (partitionCache == null)
                    {
                        throw new Exception($"partitionCache required to index Gzip content, but not provided.");
                    }

                    var gztoolIndexFilename = partitionCache.GetGztoolIndexFilename();

                    var tempgztoolIndexFilename = gztoolIndexFilename + ".wip";

                    var gzipStreamSeekable = new GZipStreamSeekable(compressedPartcloneStream, tempgztoolIndexFilename, gztoolIndexFilename);
                    uncompressedPartcloneStream = (gzipStreamSeekable, true);
                }

                if (uncompressedPartcloneStream == null && compressionInUse == Compression.Zstandard)
                {
                    //For now, let's extract it to a file so that we can have fast seeking

                    Log.Information($"Zstandard doesn't support random seeking. Extracting to a temporary file.");

                    var decompressor = new DecompressionStream(compressedPartcloneStream);

                    var tempFilename = TempUtility.GetTempFilename(true);
                    
                    var tempFileStream = new FileStream(tempFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);

                    decompressor.CopyTo(tempFileStream, Buffers.ARBITARY_HUGE_SIZE_BUFFER,
                        totalCopied =>
                        {
                            var totalCopiedStr = libCommon.Extensions.BytesToString(totalCopied);
                            Log.Information($"Extracted {totalCopiedStr} of partclone file.");
                        });

                    uncompressedPartcloneStream = (tempFileStream, false);


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

                    uncompressedPartcloneStream = new SeekableStreamUsingNearestActioner(zstdDecompressorGenerator, totalLength, 1 * 1024 * 1024);   //stations should be within one second of an actioner.
                    */
                }
            }
            else
            {
                //Doing sequential reads (not seeking)

                if (compressionInUse == Compression.None)
                {
                    uncompressedPartcloneStream = (compressedPartcloneStream, false);
                }
                else
                {
                    //this is faster for sequential reads (eg. extracting the full partition image) because it doesn't first have to generate an index, and doesn't need to run gztool for read operations.
                    var seekableStreamUsingRestarts = new SeekableStreamUsingRestarts(() =>
                    {
                        compressedPartcloneStream.Seek(0, SeekOrigin.Begin);

                        Stream newStream = compressionInUse switch
                        {
                            Compression.Gzip => new GZipStream(compressedPartcloneStream, CompressionMode.Decompress),
                            Compression.Zstandard => new DecompressionStream(compressedPartcloneStream),
                            _ => throw new Exception($"Did not initialize a stream for partition {partitionName}"),
                        };

                        return newStream;
                    });

                    uncompressedPartcloneStream = (seekableStreamUsingRestarts, false); //no cache layer required, as this is a sequential read
                }
            }

            if (uncompressedPartcloneStream != null && uncompressedPartcloneStream.Value.UseCacheLayer)
            {
                //add a cache layer
                var readSuggestor = uncompressedPartcloneStream.Value.Stream as IReadSuggestor;

                var totalSystemRAMInBytes = libCommon.Utility.GetTotalRamSizeBytes();
                var totalSystemRAMInMegabytes = (int)(totalSystemRAMInBytes / (double)(1024 * 1024));
                var maxCacheSizeInMegabytes = totalSystemRAMInMegabytes / 4;

                var uncompressedStream = uncompressedPartcloneStream.Value.Stream;
                uncompressedStream = new CachingStream(uncompressedStream, readSuggestor, EnumCacheType.LimitByRAMUsage, maxCacheSizeInMegabytes, null);

                uncompressedPartcloneStream = (uncompressedStream, false);
            }

            if (uncompressedPartcloneStream == null)
            {
                throw new Exception($"Did not initialize a stream for partition {partitionName}");
            }

            var partcloneStream = new PartcloneStream(partitionName, uncompressedPartcloneStream.Value.Stream, partcloneCache);

            FullPartitionImage = partcloneStream;
            Name = partitionName;
        }

        public void ExtractToFile(string outputFilename, bool makeSparse)
        {
            ExtractToFile(Name, FullPartitionImage, outputFilename, makeSparse);
        }

        public static void ExtractToFile(string partitionName, ISparseAwareReader sparseAwareInput, string outputFilename, bool makeSparse)
        {
            Log.Information($"[{partitionName}] Extracting partition to: {outputFilename}");

            var fileStream = File.Create(outputFilename);
            ExtractToFile(sparseAwareInput, fileStream, makeSparse);
        }

        public static void ExtractToFile(ISparseAwareReader sparseAwareInput, FileStream fileStream, bool makeSparse)
        {

            if (libCommon.Utility.IsOnNTFS(fileStream.Name) && makeSparse)
            {
                //a hack to speed things up. Let's make the output file sparse, so that we don't have to write zeroes for all the unpopulated ranges

                //tell the input stream to not bother with the remainder of the file if it's all null
                sparseAwareInput.StopReadingWhenRemainderOfFileIsNull = true;

                //tell the output stream to create a sparse file
                fileStream.SafeFileHandle.MarkAsSparse();
                fileStream.SetLength(sparseAwareInput.Length);

                //tell the writer not to bother writing the null bytes to the file (because it's already sparse)
                var outputStream = new SparseAwareWriteStream(fileStream, false);

                sparseAwareInput
                    .CopyTo(outputStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                    totalCopied =>
                    {
                        var per = (double)totalCopied / sparseAwareInput.Length * 100;

                        var totalCopiedStr = libCommon.Extensions.BytesToString(totalCopied);
                        var totalStr = libCommon.Extensions.BytesToString(sparseAwareInput.Length);
                        Log.Information($"Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                    });
            }
            else
            {
                //just a regular file, with null bytes and all
                sparseAwareInput
                    .Stream
                    .CopyTo(fileStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                    totalCopied =>
                    {
                        var per = (double)totalCopied / sparseAwareInput.Length * 100;

                        var totalCopiedStr = libCommon.Extensions.BytesToString(totalCopied);
                        var totalStr = libCommon.Extensions.BytesToString(sparseAwareInput.Length);
                        Log.Information($"Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                    });
            }
        }
    }
}
