using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.IO;
using libCommon.Streams;
using libCommon;
using libClonezilla.Cache;
using Serilog;
using libCommon.Streams.Sparse;
using ZstdNet;
using libCommon.Streams.Seekable;
using lib7Zip;
using libDokan.VFS.Folders;
using libDokan.VFS.Files;
using libClonezilla.PartitionContainers;
using libClonezilla.Extractors;

namespace libClonezilla.Partitions
{
    public abstract class Partition(PartitionContainer container, string partitionName, IPartitionCache? partitionCache, Stream? compressedOrigin)
    {
        public Stream? FullPartitionImage { get; protected set; }
        public PartitionContainer Container { get; protected set; } = container;
        public string PartitionName { get; protected set; } = partitionName;
        public IPartitionCache? PartitionCache { get; protected set; } = partitionCache;

        protected Stream? CompressedOrigin = compressedOrigin;

        /// <summary>
        /// A factory of independent read cursors over <see cref="FullPartitionImage"/>, one per native 7-Zip
        /// worker. The mount, list and extract paths all get their cursors here, so they read the same way (gh
        /// review F7: list and extract used gated views while mount read in parallel). A positional image (the
        /// compressed serving path: PartcloneStream over a CachingStream) gets lock-free cursors that each
        /// ReadAt the shared source, so the workers decode in parallel. Anything else (e.g. a raw uncompressed
        /// image file) gets SharedStream views, whose gate is held across each read.
        /// </summary>
        public Func<Stream> CreateReaderFactory()
        {
            var partitionStream = FullPartitionImage
                ?? throw new Exception($"[{Container.ContainerName}] [{PartitionName}] {nameof(FullPartitionImage)} is not initialised.");

            if (partitionStream is IPositionalReader positionalPartition)
            {
                return () => new PositionalCursorStream(positionalPartition);
            }

            var sharedPartitionStream = new SharedStream(partitionStream);
            return sharedPartitionStream.CreateView;
        }

        /// <summary>The partition's file list: from the partition cache when it holds one, otherwise enumerated
        /// by <paramref name="fileListProvider"/> and saved to the cache once fully enumerated.</summary>
        public IEnumerable<ArchiveEntry> GetFilesInPartition(IFileListProvider fileListProvider)
        {
            IEnumerable<ArchiveEntry>? archiveFiles = PartitionCache?.GetFileList();

            bool saveToCache = false;
            if (archiveFiles == null)
            {
                archiveFiles = fileListProvider.GetFileList();
                saveToCache = true;
            }

            var fullListOfFiles = new List<ArchiveEntry>();

            foreach (var archiveEntry in archiveFiles)
            {
                if (Path.GetFileName(archiveEntry.Path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue; //a micro-optimisation to stop Windows from requesting this file and causing a lot of unecessary IO

                fullListOfFiles.Add(archiveEntry);
                yield return archiveEntry;
            }

            if (saveToCache)
            {
                PartitionCache?.SetFileList(fullListOfFiles);
            }
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
