using libClonezilla.Cache;
using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using lib7Zip;
using libClonezilla.PartitionContainers.ImageFiles;
using libDokan.VFS.Folders;
using libClonezilla.VFS;
using Serilog;

namespace libClonezilla.PartitionContainers
{
    public abstract class PartitionContainer
    {
        public List<Partition> Partitions { get; protected set; } = [];

        //All partition names discovered in this container, before any -p filter is applied. Used to give a
        //helpful error (listing the valid names) when the requested partitions match nothing.
        public List<string> AvailablePartitionNames { get; protected set; } = [];

        public abstract string ContainerName { get; protected set; }

        public static PartitionContainer FromPath(string path, string cacheFolder, List<string> partitionsToLoad, bool willPerformRandomSeeking, Lazy<IVFS> vfs, bool processTrailingNulls)
        {
            PartitionContainer? result = null;

            if (Directory.Exists(path))
            {
                var clonezillaMagicFile = Path.Combine(path, "clonezilla-img");

                if (File.Exists(clonezillaMagicFile))
                {
                    var clonezillaCacheManager = new ClonezillaCacheManager(path, cacheFolder);
                    result = new ClonezillaImage(path, clonezillaCacheManager, partitionsToLoad, willPerformRandomSeeking, processTrailingNulls);
                }
            }
            else if (File.Exists(path))
            {
                result = new ImageFile(path, partitionsToLoad, willPerformRandomSeeking, vfs, processTrailingNulls);
            }
            else
            {
                throw new Exception($"File not found: {path}");
            }

            if (result == null)
            {
                throw new Exception($"Could not determine if this is a Clonezilla folder, or a partclone file: {path}");
            }

            return result;
        }

        public static List<PartitionContainer> FromPaths(List<string> paths, string cacheFolder, List<string> partitionsToLoad, bool willPerformRandomSeeking, Lazy<IVFS> vfs, bool processTrailingNulls)
        {
            var result = paths
                            .Select(path => FromPath(path, cacheFolder, partitionsToLoad, willPerformRandomSeeking, vfs, processTrailingNulls))
                            .ToList();

            ValidateRequestedPartitions(partitionsToLoad, result);

            return result;
        }

        //If the user asked for specific partitions (-p), every one of them must exist across the loaded
        //containers. Bail out (rather than silently ignoring the typo) listing the bad names and the valid ones.
        static void ValidateRequestedPartitions(List<string> requestedPartitionNames, List<PartitionContainer> containers)
        {
            if (requestedPartitionNames.Count == 0)
            {
                //no filter → serve everything
                return;
            }

            var availablePartitionNames = containers
                                            .SelectMany(container => container.AvailablePartitionNames)
                                            .Distinct()
                                            .ToList();

            var invalidPartitionNames = requestedPartitionNames
                                            .Where(requested => !availablePartitionNames.Contains(requested))
                                            .Distinct()
                                            .ToList();

            if (invalidPartitionNames.Count > 0)
            {
                var availableSorted = availablePartitionNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
                Log.Error($"Invalid partition(s) requested: {string.Join(", ", invalidPartitionNames)}. Valid partitions are: {string.Join(", ", availableSorted)}. Exiting.");
                Environment.Exit(1);
            }
        }
    }
}