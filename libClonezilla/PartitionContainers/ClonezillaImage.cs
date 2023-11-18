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

            var partitionNames = PartitionsInFolder(clonezillaArchiveFolder);

            Partitions = partitionNames
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
                                var splitFilenames = Directory
                                                        .GetFiles(clonezillaArchiveFolder, $"{partitionName}.*-ptcl-img*")
                                                        .ToList();

                                compressionInUse = GetCompressionInUse(splitFilenames.First());


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
                containerName ??= Path.GetFileName(ClonezillaArchiveFolder) ?? throw new Exception($"Could not get container name from path: {ClonezillaArchiveFolder}");

                return containerName;
            }

            protected set
            {
                containerName = value;
            }
        }
    }
}
