using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers
{
    public class PartcloneFile : PartitionContainer
    {
        public string Filename { get; }

        public PartcloneFile(string filename, bool willPerformRandomSeeking)
        {
            Filename = filename;

            var partitionName = Path.GetFileName(filename);

            var partition = new Partition(this, File.OpenRead(filename), libPartclone.Compression.None, partitionName, null, null, willPerformRandomSeeking);

            Partitions = new List<Partition>
            {
                partition
            };
        }

        public override string GetName()
        {
            var containerName = Path.GetFileName(Filename) ?? throw new Exception($"Could not get container name from path: {Filename}");
            return containerName;
        }
    }
}
