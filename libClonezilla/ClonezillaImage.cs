using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace libClonezilla
{
    public class ClonezillaImage
    {
        public List<Partition> Partitions;

        public ClonezillaImage(string folder, IClonezillaCacheManager cacheManager, IEnumerable<string>? partitionsToLoad, bool willPerformRandomSeeking)
        {
            var clonezillaArchiveName = Path.GetFileName(folder);
            var partsFile = Path.Combine(folder, "parts");

            Partitions = File
                            .ReadAllText(partsFile)
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
                            .SelectMany(partitionName =>
                            {
                                var partitions = new List<Partition>();

                                var gzipFilenames = Directory.GetFiles(folder, $"{partitionName}.*-ptcl-img.gz.*")
                                                    .OrderBy(filename => filename)
                                                    .ToList();

                                var firstGzip = gzipFilenames.FirstOrDefault();

                                if (firstGzip != null)
                                {
                                    var partitionType = Path.GetFileName(firstGzip).Split('.', '-')[1];

                                    var partitionCache = cacheManager.GetPartitionCache(folder, partitionName);

                                    var result = Partition.GetPartition(clonezillaArchiveName, gzipFilenames, partitionName, partitionType, partitionCache, willPerformRandomSeeking);

                                    partitions.Add(result);
                                }

                                return partitions;
                            })
                            .ToList();
        }
    }
}
