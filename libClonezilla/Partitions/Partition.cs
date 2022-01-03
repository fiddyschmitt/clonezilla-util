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

        public static (Compression compression, List<string> containerFilenames) GetCompressionInUse(string clonezillaArchiveFolder, string partitionName)
        {
            var compressionPatterns = new (Compression Compression, string FilenamePattern)[]
            {
                (Compression.Gzip, $"{partitionName}.*-ptcl-img.gz.*"),
                (Compression.Zstandard, $"{partitionName}.*-ptcl-img.zst.*"),
            };

            foreach (var pattern in compressionPatterns)
            {
                var files = Directory
                                .GetFiles(clonezillaArchiveFolder, pattern.FilenamePattern)
                                .OrderBy(filename => filename)
                                .ToList();

                if (files.Count > 0)
                {
                    var result = (pattern.Compression, files);
                    return result;
                }
            }

            throw new Exception($"Could not determine compression used by partition {partitionName} in: {clonezillaArchiveFolder}");
        }

        public static Partition GetPartition(string clonezillaArchiveName, string clonezillaArchiveFolder, string partitionName, IPartitionCache partitionCache, bool willPerformRandomSeeking)
        {
            Log.Information($"Getting partition information for {partitionName}");

            //determine the type of compression in use
            (var compressionInUse, var containerFilenames) = GetCompressionInUse(clonezillaArchiveFolder, partitionName);

            var firstContainerFilename = containerFilenames.First();

            var partitionType = Path.GetFileName(firstContainerFilename).Split('.', '-')[1];

            var containerStreams = containerFilenames
                                    .Select(filename => new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    .ToList();
            var compressedStream = new Multistream(containerStreams);

            PartcloneStream? fullPartitionImage = null;
            if (willPerformRandomSeeking && compressionInUse != Compression.Zstandard)
            {
                if (compressionInUse == Compression.Gzip)
                {
                    //this is faster for random seeks (eg. serving the full image (or file contents) in a Virtual File System)
                    //Uses gztool to create an index for fast seek, plus a cache layer to avoid using gztool for small reads

                    var gztoolIndexFilename = partitionCache.GetGztoolIndexFilename();
                    var tempgztoolIndexFilename = gztoolIndexFilename + ".wip";

                    var fastSeektable = new GZipStreamSeekable(compressedStream, tempgztoolIndexFilename, gztoolIndexFilename);
                    IReadSegmentSuggestor suggestor = fastSeektable;

                    var totalSystemRAMInBytes = libCommon.Utility.GetTotalRamSizeBytes();
                    var totalSystemRAMInMegabytes = (int)(totalSystemRAMInBytes / (double)(1024 * 1024));
                    var maxCacheSizeInMegabytes = (int)(totalSystemRAMInMegabytes / 8d);
                    var cachingStream = new CachingStream(fastSeektable, suggestor, EnumCacheType.LimitByRAMUsage, maxCacheSizeInMegabytes, null);
                    fullPartitionImage = new PartcloneStream(clonezillaArchiveName, partitionName, cachingStream, partitionCache);
                }
            }
            else
            {
                //this is faster for sequential reads (eg. extracting the full partition image) because it doesn't first have to generate an index, and doesn't need to run gztool for read operations.
                //Also, haven't yet implemented a way of efficiently seeking Zstandard, so we'll have to use the slow way for sequential & random for Zstandard for now.

                var slowSeekable = new SeekableStream(() =>
                {
                    compressedStream.Seek(0, SeekOrigin.Begin);
                    Stream newStream = compressionInUse switch
                    {
                        Compression.Gzip => new GZipStream(compressedStream, CompressionMode.Decompress),
                        Compression.Zstandard => new DecompressionStream(compressedStream),
                        _ => throw new Exception($"Did not initialize a stream for partition {partitionName} in: {clonezillaArchiveFolder}"),
                    };
                    return newStream;
                });

                fullPartitionImage = new PartcloneStream(clonezillaArchiveName, partitionName, slowSeekable, partitionCache);
            }

            if (fullPartitionImage == null)
            {
                throw new Exception($"Did not initialize a stream for partition {partitionName} in: {clonezillaArchiveFolder}");
            }

            Partition result = new BasicPartition(partitionName, partitionType, fullPartitionImage);

            return result;
        }

        public abstract IEnumerable<FolderDetails> GetFoldersInPartition();
        public abstract IEnumerable<FileDetails> GetFilesInPartition();
        public abstract Stream? GetFile(string filename);

        public void ExtractToFile(string outputFilename, bool makeSparse)
        {
            Log.Information($"Extracting partition {Name} to: {outputFilename}");

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

        public enum Compression
        {
            Gzip,
            Zstandard
        }
    }
}
