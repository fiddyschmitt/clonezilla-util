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
        public RawPartitionImage(string originFilename, string containerName, List<string> partitionsToLoad, string partitionName, Stream rawStream, bool processTrailingNulls)
        {
            ContainerName = containerName;
            rawStream.Seek(0, SeekOrigin.Begin);

            var partition = new ImageFilePartition(originFilename, this, partitionName, rawStream, rawStream.Length, Compression.None, null, true, processTrailingNulls);

            Partitions = [];

            if (partitionsToLoad.Count == 0 || partitionsToLoad.Contains(partitionName))
            {
                Partitions.Add(partition);
            };
        }

        public override string ContainerName { get; protected set; }
    }
}
