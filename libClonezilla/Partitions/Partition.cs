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

namespace libClonezilla.Partitions
{
    public abstract class Partition
    {
        public string Name;
        public string Type;

        public Partition(string name, string type, Stream fullPartitionImage)
        {
            Name = name;
            Type = type;
            FullPartitionImage = fullPartitionImage;
        }

        public Stream FullPartitionImage { get; }

        public static Partition GetPartition(string clonezillaArchiveName, List<string> gzipFilenames, string partitionName, string type, IPartitionCache partitionCache, bool willPerformRandomSeeking)
        {
            Log.Information($"Getting partition information for {partitionName}");

            var containerStreams = gzipFilenames
                                    .Select(filename => new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    .ToList();

            var gztoolIndexFilename = partitionCache.GetGztoolIndexFilename();
            var tempgztoolIndexFilename = gztoolIndexFilename + ".wip";

            var compressedStream = new Multistream(containerStreams);

            Stream uncompressedStream;
            IReadSegmentSuggestor? suggestor = null;

            PartcloneStream fullPartitionImage;
            if (willPerformRandomSeeking)
            {
                //this is faster for random seeks (eg. serving the full image (or file contents) in a Virtual File System)
                //Uses gztool to create an index for fast seek, plus a cache layer to avoid using gztool for small reads

                var fastSeektable = new GZipStreamSeekable(compressedStream, tempgztoolIndexFilename, gztoolIndexFilename);
                uncompressedStream = fastSeektable;

                var totalSystemRAMInBytes = libCommon.Utility.GetTotalRamSizeBytes();
                var totalSystemRAMInMegabytes = (int)(totalSystemRAMInBytes / (double)(1024 * 1024));
                var maxCacheSizeInMegabytes = (int)(totalSystemRAMInMegabytes / 8d);
                var cachingStream = new CachingStream(uncompressedStream, suggestor, EnumCacheType.LimitByRAMUsage, maxCacheSizeInMegabytes, null);
                fullPartitionImage = new PartcloneStream(clonezillaArchiveName, partitionName, cachingStream, partitionCache);

            }
            else
            {
                //this is faster for sequential reads (eg. extracting the full partition image)

                var slowSeekable = new SeekableStream(() =>
                {
                    compressedStream.Seek(0, SeekOrigin.Begin);
                    var newStream = new GZipStream(compressedStream, CompressionMode.Decompress);

                    return newStream;
                });

                fullPartitionImage = new PartcloneStream(clonezillaArchiveName, partitionName, slowSeekable, partitionCache);
            }


            Partition result = new BasicPartition(partitionName, type, fullPartitionImage);

            return result;
        }

        public abstract IEnumerable<FolderDetails> GetFoldersInPartition();
        public abstract IEnumerable<FileDetails> GetFilesInPartition();
        public abstract Stream? GetFile(string filename);

        public void ExtractToFile(string outputFilename, bool makeSparse)
        {
            Log.Information($"Extracting partition {Name}");
            Log.Information($"To: {outputFilename}");

            var fileStream = File.Create(outputFilename);
            ISparseAwareWriter outputStream;

            if (FullPartitionImage is ISparseAwareReader inputStream)
            {
                //a hack to speed things up. Let's make the output file sparse, so that we don't have to write zeroes for all the unpopulated ranges
                if (libCommon.Utility.IsOnNTFS(outputFilename) && makeSparse)
                {
                    //tell the input stream to not bother with the remainder of the file if it's all null
                    inputStream.StopReadingWhenRemainderOfFileIsNull = true;

                    //tell the output stream to create a sparse file
                    fileStream.SafeFileHandle.MarkAsSparse();
                    fileStream.SetLength(FullPartitionImage.Length);

                    //tell the writer not to bother writing the null bytes to the file (because it's already sparse)
                    outputStream = new SparseAwareWriteStream(fileStream, false);
                }
                else
                {
                    inputStream = new SparseAwareReader(FullPartitionImage, true);
                    outputStream = new SparseAwareWriteStream(fileStream, true);
                }

                inputStream
                    .CopyTo(outputStream, Buffers.SUPER_ARBITARY_LARGE_SIZE_BUFFER,
                    totalCopied =>
                    {
                        var per = (double)totalCopied / FullPartitionImage.Length * 100;

                        var totalCopiedStr = libCommon.Extensions.BytesToString(totalCopied);
                        var totalStr = libCommon.Extensions.BytesToString(FullPartitionImage.Length);
                        Log.Information($"Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                    });
            }
        }
    }
}
