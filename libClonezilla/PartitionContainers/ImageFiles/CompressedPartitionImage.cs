using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class CompressedPartitionImage : PartitionContainer
    {
        public CompressedPartitionImage(string containerName, Stream rawStream)
        {
            ContainerName = containerName;
        }

        public override string ContainerName { get; protected set; }
    }
}
