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
        public RawPartitionImage(string containerName, string partitionName, Stream rawStream)
        {
            ContainerName = containerName;
            rawStream.Seek(0, SeekOrigin.Begin);

            var partition = new ImageFilePartition(this, partitionName, rawStream, rawStream.Length, Compression.None, null, true);

            Partitions = new()
            {
                partition
            };
        }

        public override string ContainerName { get; protected set; }
    }
}
