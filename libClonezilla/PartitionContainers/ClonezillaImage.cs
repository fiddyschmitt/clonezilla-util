using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
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
            var containerName = GetName();

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

                                if (!File.Exists(drivePartitionsFilename))
                                {
                                    throw new Exception($"Could not find the drive partitions file: {drivePartitionsFilename}");
                                }

                                /*
                                //get the original size of the partition
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
                                var partitionSizeInBytes = partitionSizeInSectors * sectorSizeInBytes;
                                */


                                var partitionCache = cacheManager.GetPartitionCache(partitionName);
                                var partcloneCache = partitionCache as IPartcloneCache;

                                //determine the type of compression in use
                                (var compressionInUse, var containerFilenames) = GetCompressionInUse(clonezillaArchiveFolder, partitionName);

                                var firstContainerFilename = containerFilenames.First();

                                var partitionType = Path.GetFileName(firstContainerFilename).Split('.', '-')[1];

                                var containerStreams = containerFilenames
                                                        .Select(filename => new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                                        .ToList();

                                var compressedPartcloneStream = new Multistream(containerStreams);

                                Partition? result = null;

                                try
                                {
                                    result = new Partition(this, compressedPartcloneStream, compressionInUse, partitionName, partitionCache, partcloneCache, willPerformRandomSeeking);
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

        public override string GetName()
        {
            var containerName = Path.GetFileName(ClonezillaArchiveFolder) ?? throw new Exception($"Could not get container name from path: {ClonezillaArchiveFolder}");
            return containerName;
        }

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
    }
}
