using libClonezilla.Cache;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libCommon.Streams;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace libClonezilla.PartitionContainers
{
    public class ClonezillaImage : PartitionContainer
    {
        public string ClonezillaArchiveFolder { get; }

        public ClonezillaImage(
            string clonezillaArchiveFolder, 
            IClonezillaCacheManager cacheManager, 
            List<string> partitionsToLoad, 
            bool willPerformRandomSeeking,
            bool processTrailingNulls)
        {
            ClonezillaArchiveFolder = clonezillaArchiveFolder;

            var partitionNames = PartitionsInFolder(clonezillaArchiveFolder);
            AvailablePartitionNames = partitionNames;

            Partitions = partitionNames
                            .Where(partitionName => partitionsToLoad.Count == 0 || partitionsToLoad.Contains(partitionName))
                            .Select(partitionName => OpenPartition(clonezillaArchiveFolder, cacheManager, partitionName, willPerformRandomSeeking, processTrailingNulls))
                            .OfType<Partition>()
                            .ToList();
        }

        //Everything that can fail for one partition - locating its files, identifying the compression, opening
        //them, and reading the partclone header - happens inside this try, so one bad partition is skipped with a
        //clear message instead of aborting the whole folder (gh review F9). Clonezilla can also write lzo, lrz and
        //lzma images, which this tool cannot decode. A failed partition's file handles are released immediately.
        Partition? OpenPartition(string clonezillaArchiveFolder, IClonezillaCacheManager cacheManager, string partitionName, bool willPerformRandomSeeking, bool processTrailingNulls)
        {
            var splitFileStreams = new List<FileStream>();
            try
            {
                var partitionCache = cacheManager.GetPartitionCache(partitionName);

                var splitFilenames = Directory
                                        .GetFiles(clonezillaArchiveFolder, $"{partitionName}.*-ptcl-img*")
                                        .OrderBy(filename => filename, StringComparer.OrdinalIgnoreCase)   //GetFiles does not guarantee order; split volumes (.aa, .ab, ...) must be concatenated in sequence
                                        .ToList();
                if (splitFilenames.Count == 0)
                {
                    throw new FileNotFoundException($"No image files found for partition {partitionName}");
                }

                var compressionInUse = GetCompressionInUse(splitFilenames[0]);
                var used_dd = splitFilenames[0].Contains(".dd-ptcl-img");

                foreach (var filename in splitFilenames)
                {
                    splitFileStreams.Add(new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                }

                var compressedPartcloneStream = new Multistream(splitFileStreams);

                if (used_dd)
                {
                    //the partition is stored using dd
                    return new ImageFilePartition(
                        clonezillaArchiveFolder,
                        this,
                        partitionName,
                        compressedPartcloneStream,
                        null,
                        compressionInUse,
                        partitionCache,
                        willPerformRandomSeeking,
                        processTrailingNulls);
                }

                return new PartclonePartition(
                    clonezillaArchiveFolder,
                    this,
                    partitionName,
                    compressedPartcloneStream,
                    compressionInUse,
                    partitionCache,
                    willPerformRandomSeeking,
                    processTrailingNulls);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[{ContainerName}] [{partitionName}] Could not open this partition; skipping it.");
                foreach (var stream in splitFileStreams)
                {
                    stream.Dispose();
                }
                return null;
            }
        }

        public static List<string> PartitionsInFolder(string clonezillaArchiveFolder)
        {
            var partitionNames = Directory
                            .GetFiles(clonezillaArchiveFolder, "*-ptcl-img*")
                            .GroupBy(
                                filename => Path.GetFileName(filename).Split('.').First(),
                                filename => filename,
                                (k, g) => k)
                            .ToList();

            return partitionNames;
        }

        public static Compression GetCompressionInUse(string filename)
        {
            var extension = Path.GetExtension(filename);
            if (extension.Equals(".aa"))
            {
                //when an image is split into multiple files, it gets filenames such as these. Inspect the extension to the left
                extension = Path.GetExtension(Path.GetFileNameWithoutExtension(filename));
            }

            var result = extension switch
            {
                ".bz2" => Compression.bzip2,
                ".gz" => Compression.Gzip,
                ".lz4" => Compression.LZ4,
                ".lzip" => Compression.LZip,
                ".uncomp" => Compression.None,
                ".xz" => Compression.xz,
                ".zst" => Compression.Zstandard,
                _ => throw new Exception($"Could not determine compression used by partition stored in {filename}")
            };

            return result;
        }

        string? containerName;
        public override string ContainerName
        {
            get
            {
                containerName ??= libClonezilla.Utility.ContainerNameFromPath(ClonezillaArchiveFolder);

                return containerName;
            }

            protected set
            {
                containerName = value;
            }
        }
    }
}
