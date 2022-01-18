using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libPartclone;
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

            var partcloneStream = File.OpenRead(filename);

            var partcloneInfo = new PartcloneImageInfo(Name, partitionName, partcloneStream, null);
            var uncompressedLength = partcloneInfo.Length;

            var partition = new PartclonePartition(this, partitionName, partcloneStream, uncompressedLength, Compression.None, null, null, willPerformRandomSeeking);

            Partitions = new List<Partition>
            {
                partition
            };
        }

        string? containerName;
        public override string Name
        {
            get
            {
                if (containerName == null)
                {
                    containerName = Path.GetFileName(Filename) ?? throw new Exception($"Could not get container name from path: {Filename}");
                }

                return containerName;
            }

            set
            {
                containerName = value;
            }
        }
    }
}
