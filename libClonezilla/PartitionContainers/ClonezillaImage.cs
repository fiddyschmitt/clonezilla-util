using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libCommon.Streams;
using libPartclone;
using libPartclone.Cache;
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

        public ClonezillaImage(string clonezillaArchiveFolder, IClonezillaCacheManager cacheManager, List<string> partitionsToLoad, bool willPerformRandomSeeking)
        {
            ClonezillaArchiveFolder = clonezillaArchiveFolder;

            var partsFilename = Path.Combine(clonezillaArchiveFolder, "parts");

            if (!File.Exists(partsFilename))
            {
                throw new Exception($"Could not find the partitions list file: {partsFilename}");
            }

            Partitions = File
                            .ReadAllText(partsFilename)
                            .Split(' ')
                            .Select(partitionName => partitionName.Trim())
                            .Where(partitionName => partitionsToLoad.Count == 0 || partitionsToLoad.Contains(partitionName))
                            .Select(partitionName =>
                            {
                                var driveName = new string(partitionName.TakeWhile(c => !char.IsNumber(c)).ToArray());

                                var drivePartitionsFilename = Path.Combine(clonezillaArchiveFolder, $"{driveName}-pt.sf");

                                //get the original size of the partition
                                long? partitionSizeInBytes = null;
                                try
                                {
                                    if (!File.Exists(drivePartitionsFilename))
                                    {
                                        throw new Exception($"Could not find the drive partitions file: {drivePartitionsFilename}");
                                    }

                                    var lines = File.ReadAllLines(drivePartitionsFilename);

                                    var sectorSizeInBytesStr = lines
                                                                .First(line => line.StartsWith("sector-size"))
                                                                .Split(':')
                                                                .Last()
                                                                .Trim();

                                    var sectorSizeInBytes = int.Parse(sectorSizeInBytesStr);

                                    var partitionSizeInSectorsString = lines
                                                                        .First(line => line.StartsWith($"/dev/{partitionName}"))
                                                                        .Split("size=")[1]
                                                                        .Split(",")[0]
                                                                        .Trim();

                                    var partitionSizeInSectors = long.Parse(partitionSizeInSectorsString);
                                    partitionSizeInBytes = partitionSizeInSectors * sectorSizeInBytes;
                                }
                                catch
                                {
                                    Log.Debug($"Could not get sector size from: {drivePartitionsFilename}");
                                }


                                var partitionCache = cacheManager.GetPartitionCache(partitionName);
                                var partcloneCache = partitionCache as IPartcloneCache;


                                Compression compressionInUse = Compression.None;
                                var splitFilenames = new List<string>();

                                var luksInfoFile = Path.Combine(clonezillaArchiveFolder, "luks-dev.list");
                                if (File.Exists(luksInfoFile))
                                {
                                    var luksFilename = File
                                                        .ReadAllLines(luksInfoFile)
                                                        .First(line => line.StartsWith($"/dev/{partitionName}"))
                                                        .Split(new[] { "/" }, StringSplitOptions.None)
                                                        .LastOrDefault();

                                    splitFilenames = Directory
                                                        .GetFiles(clonezillaArchiveFolder, $"*{luksFilename}.*-ptcl-img*")
                                                        .ToList();

                                    compressionInUse = GetCompressionInUse(clonezillaArchiveFolder, $"*{luksFilename}.*-ptcl-img");
                                }
                                else
                                {
                                    splitFilenames = Directory
                                                        .GetFiles(clonezillaArchiveFolder, $"{partitionName}.*-ptcl-img*")
                                                        .ToList();

                                    compressionInUse = GetCompressionInUse(clonezillaArchiveFolder, $"{partitionName}.*-ptcl-img");
                                }


                                var splitFileStreams = splitFilenames
                                                        .Select(filename => new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                                        .ToList();

                                var compressedPartcloneStream = new Multistream(splitFileStreams);

                                Partition? result = null;

                                try
                                {
                                    result = new PartclonePartition(clonezillaArchiveFolder, this, partitionName, compressedPartcloneStream, partitionSizeInBytes, compressionInUse, partitionCache, partcloneCache, willPerformRandomSeeking);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, $"[{containerName}] [{partitionName}] Error while opening partition");
                                }

                                return result;
                            })
                            .OfType<Partition>()
                            .ToList();
        }

        public static Compression GetCompressionInUse(string clonezillaArchiveFolder, string filenamePattern)
        {
            var compressionPatterns = new (Compression Compression, string FilenamePattern)[]
            {
                (Compression.bzip2, $"{filenamePattern}.bz2.*"),
                (Compression.Gzip, $"{filenamePattern}.gz.*"),
                (Compression.LZ4, $"{filenamePattern}.lz4.*"),
                (Compression.LZip, $"{filenamePattern}.lzip.*"),
                (Compression.None, $"{filenamePattern}.uncomp.*"),
                (Compression.xz, $"{filenamePattern}.xz.*"),
                (Compression.Zstandard, $"{filenamePattern}.zst.*"),
            };

            foreach (var pattern in compressionPatterns)
            {
                var files = Directory
                                .GetFiles(clonezillaArchiveFolder, pattern.FilenamePattern)
                                .OrderBy(filename => filename)
                                .ToList();

                if (files.Count > 0)
                {
                    var result = pattern.Compression;
                    return result;
                }
            }
            throw new Exception($"Could not determine compression used by partition {filenamePattern} in: {clonezillaArchiveFolder}");
        }

        string? containerName;
        public override string ContainerName
        {
            get
            {
                if (containerName == null)
                {
                    containerName = Path.GetFileName(ClonezillaArchiveFolder) ?? throw new Exception($"Could not get container name from path: {ClonezillaArchiveFolder}");
                }

                return containerName;
            }

            protected set
            {
                containerName = value;
            }
        }
    }
}
