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

        public static Partition GetPartition(string clonezillaArchiveName, List<string> gzipFilenames, string partitionName, string type, IPartitionCache? partitionCache)
        {
            var containerStreams = gzipFilenames
                                    .Select(filename => new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                    .ToList();            

            var gztoolIndexFilename = partitionCache?.GetGztoolIndexFilename();

            var compressedStream = new Multistream(containerStreams);

            Stream uncompressedStream;
            IReadSegmentSuggestor? suggestor = null;

            if (gztoolIndexFilename == null)
            {
                var slowSeekable = new SeekableStream(() =>
                {
                    compressedStream.Seek(0, SeekOrigin.Begin);
                    var newStream = new GZipStream(compressedStream, CompressionMode.Decompress);

                    return newStream;
                });

                uncompressedStream = slowSeekable;
            }
            else
            {
                var fastSeektable = new GZipStreamSeekable(compressedStream, gztoolIndexFilename);
                uncompressedStream = fastSeektable;
                suggestor = fastSeektable;
            }


            //todo: only introduce this cache level if it's required.
            //var totalSystemRAMInBytes = libCommon.Utility.GetTotalRamSizeBytes();
            //var totalSystemRAMInMegabytes = (int)(totalSystemRAMInBytes / (double)(1024 * 1024));
            //var maxCacheSizeInMegabytes = (int)(totalSystemRAMInMegabytes / 8d);

            //var cachingStream = new CachingStream(uncompressedStream, suggestor, EnumCacheType.LimitByRAMUsage, maxCacheSizeInMegabytes, null);
            //var fullPartitionImage = new PartcloneStream(clonezillaArchiveName, partitionName, cachingStream);

            var fullPartitionImage = new PartcloneStream(clonezillaArchiveName, partitionName, uncompressedStream);


            Partition result = new BasicPartition(partitionName, type, fullPartitionImage);

            return result;
        }

        public abstract IEnumerable<FolderDetails> GetFoldersInPartition();
        public abstract IEnumerable<FileDetails> GetFilesInPartition();
        public abstract Stream? GetFile(string filename);
    }
}
