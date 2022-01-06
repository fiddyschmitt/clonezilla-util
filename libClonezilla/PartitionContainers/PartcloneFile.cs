using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers
{
    public class PartcloneFile : IPartitionContainer
    {
        public List<Partition> Partitions { get; }
        public string Filename { get; }

        public PartcloneFile(string filename, bool willPerformRandomSeeking)
        {
            Filename = filename;

            var partitionName = Path.GetFileName(filename);

            var partition = new Partition(File.OpenRead(filename), libPartclone.Compression.None, partitionName, null, null, willPerformRandomSeeking);

            Partitions = new List<Partition>
            {
                partition
            };
        }
    }
}
