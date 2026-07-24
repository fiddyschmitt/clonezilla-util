using libClonezilla.Cache;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libCommon.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class RawPartitionImage : PartitionContainer
    {
        public RawPartitionImage(string originFilename, string containerName, List<string> partitionsToLoad, string partitionName, Stream rawStream, bool processTrailingNulls, string? wholeFileCacheFolder)
        {
            ContainerName = containerName;
            rawStream.Seek(0, SeekOrigin.Begin);

            //no clonezilla-style cache folder exists for a bare image file; the whole-file identity
            //folder stands in, so the partition still gets a cached file list and serving decision
            IPartitionCache? partitionCache = null;
            if (wholeFileCacheFolder != null)
            {
                partitionCache = new PartitionCache(wholeFileCacheFolder, partitionName);
            }

            var partition = new ImageFilePartition(originFilename, this, partitionName, rawStream, rawStream.Length, Compression.None, partitionCache, true, processTrailingNulls);

            AvailablePartitionNames = [partitionName];
            Partitions = [];

            if (partitionsToLoad.Count == 0 || partitionsToLoad.Contains(partitionName))
            {
                Partitions.Add(partition);
            };
        }

        public override string ContainerName { get; protected set; }
    }
}
