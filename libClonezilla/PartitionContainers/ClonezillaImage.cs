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

        public ClonezillaImage(string clonezillaArchiveFolder, IClonezillaCacheManager cacheManager, IEnumerable<string>? partitionsToLoad, bool willPerformRandomSeeking)
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
                            .Where(partitionName =>
                            {
                                if (partitionsToLoad == null || !partitionsToLoad.Any())
                                {
                                    //the user did not specify any particular partitions
                                    return true;
                                }

                                if (partitionsToLoad.Any(p => p.Equals(partitionName)))
                                {
                                    //the user specified some partitions, and this is one of them
                                    return true;
                                }

                                return false;
                            })
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

                                var splitFilenames = Directory
                                                        .GetFiles(clonezillaArchiveFolder, $"{partitionName}.*-ptcl-img*")
                                                        .ToList();

                                var compressionInUse = GetCompressionInUse(clonezillaArchiveFolder, partitionName);

                                var splitFileStreams = splitFilenames
                                                        .Select(filename => new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                                        .ToList();

                                var compressedPartcloneStream = new Multistream(splitFileStreams);

                                Partition? result = null;

                                try
                                {
                                    result = new PartclonePartition(this, partitionName, compressedPartcloneStream, partitionSizeInBytes, compressionInUse, partitionCache, partcloneCache, willPerformRandomSeeking);
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

        public static Compression GetCompressionInUse(string clonezillaArchiveFolder, string partitionName)
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
                    var result = pattern.Compression;
                    return result;
                }
            }
            throw new Exception($"Could not determine compression used by partition {partitionName} in: {clonezillaArchiveFolder}");
        }

        string? containerName;
        public override string Name
        {
            get
            {
                if (containerName == null)
                {
                    containerName = Path.GetFileName(ClonezillaArchiveFolder) ?? throw new Exception($"Could not get container name from path: {ClonezillaArchiveFolder}");
                }

                return containerName;
            }

            set
            {
                containerName = value;
            }
        }
    }
}
