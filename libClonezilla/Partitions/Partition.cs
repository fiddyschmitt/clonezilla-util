using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.IO;
using libCommon.Streams;
using libGZip;
using libCommon;
using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
using Serilog;
using libCommon.Streams.Sparse;
using ZstdNet;
using libCommon.Streams.Seekable;
using lib7Zip;
using libDokan.VFS.Folders;
using libDokan.VFS.Files;
using libClonezilla.PartitionContainers;

namespace libClonezilla.Partitions
{
    public abstract class Partition
    {
        public Stream? FullPartitionImage { get; protected set; }
        public PartitionContainer Container { get; protected set; }
        public string PartitionName { get; protected set; }
        public IPartitionCache? PartitionCache { get; protected set; }

        protected Stream? CompressedOrigin = null;

        public Partition(PartitionContainer container, string partitionName, IPartitionCache? partitionCache, Stream? compressedOrigin)
        {
            Container = container;
            PartitionName = partitionName;
            PartitionCache = partitionCache;
            CompressedOrigin = compressedOrigin;
        }

        public void ExtractToFile(string outputFilename, bool makeSparse)
        {
            if (FullPartitionImage == null) throw new Exception($"[{Container.ContainerName}] [{PartitionName}] Cannot extract. {nameof(FullPartitionImage)} has not been intialised.");

            ExtractToFile(Container.ContainerName, PartitionName, FullPartitionImage, outputFilename, makeSparse, CompressedOrigin);
        }

        public static void ExtractToFile(string containerName, string partitionName, Stream stream, string outputFilename, bool makeSparse, Stream? compressedOrigin)
        {
            Log.Information($"[{containerName}] [{partitionName}] Extracting partition to: {outputFilename}");

            using var fileStream = File.Create(outputFilename);
            StreamUtility.ExtractToFile($"[{containerName}] [{partitionName}]", compressedOrigin, stream, fileStream, makeSparse);
        }
    }
}
