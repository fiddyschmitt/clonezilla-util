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

        public PartcloneFile(string filename, List<string> partitionsToLoad, bool willPerformRandomSeeking)
        {
            Filename = filename;

            var partitionName = Path.GetFileName(filename);

            var partcloneStream = File.OpenRead(filename);

            var partcloneInfo = new PartcloneImageInfo(ContainerName, partitionName, partcloneStream, null);
            var uncompressedLength = partcloneInfo.Length;

            Partitions = new();

            if (partitionsToLoad.Count == 0 || partitionsToLoad.Contains(partitionName))
            {
                var partition = new PartclonePartition(filename, this, partitionName, partcloneStream, uncompressedLength, Compression.None, null, null, willPerformRandomSeeking);
                Partitions.Add(partition);
            };
        }

        string? containerName;
        public override string ContainerName
        {
            get
            {
                if (containerName == null)
                {
                    containerName = Path.GetFileName(Filename) ?? throw new Exception($"Could not get container name from path: {Filename}");
                }

                return containerName;
            }

            protected set
            {
                containerName = value;
            }
        }
    }
}
