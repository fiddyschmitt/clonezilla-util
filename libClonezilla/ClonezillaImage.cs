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

        public ClonezillaImage(string folder, IClonezillaCacheManager? cacheManager, IEnumerable<string>? partitionsToLoad)
        {
            var clonezillaArchiveName = Path.GetFileName(folder);
            var partsFile = Path.Combine(folder, "parts");

            Partitions = File
                            .ReadAllText(partsFile)
                            .Split(' ')
                            .Select(partitionName => partitionName.Trim())
                            .Where(partitionName => partitionsToLoad?.Any(p => p.Equals(partitionName, StringComparison.CurrentCultureIgnoreCase)) ?? true)
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

                                    var partitionCache = cacheManager?.GetPartitionCache(folder, partitionName);

                                    var result = Partition.GetPartition(clonezillaArchiveName, gzipFilenames, partitionName, partitionType, partitionCache);

                                    partitions.Add(result);
                                }

                                return partitions;
                            })
                            .ToList();
        }
    }
}
