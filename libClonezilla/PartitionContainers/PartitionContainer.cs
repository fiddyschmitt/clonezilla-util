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

namespace libClonezilla.PartitionContainers
{
    public abstract class PartitionContainer
    {
        public List<Partition> Partitions { get; protected set; } = [];

        public abstract string ContainerName { get; protected set; }

        public static PartitionContainer FromPath(string path, string cacheFolder, List<string> partitionsToLoad, bool willPerformRandomSeeking, IVFS vfs, bool processTrailingNulls)
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

        public static List<PartitionContainer> FromPaths(List<string> paths, string cacheFolder, List<string> partitionsToLoad, bool willPerformRandomSeeking, IVFS vfs, bool processTrailingNulls)
        {
            var result = paths
                            .Select(path => FromPath(path, cacheFolder, partitionsToLoad, willPerformRandomSeeking, vfs, processTrailingNulls))
                            .ToList();

            return result;
        }
    }
}