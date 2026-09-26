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

        public PartcloneFile(string filename, List<string> partitionsToLoad, bool willPerformRandomSeeking, bool processTrailingNulls)
        {
            Filename = filename;

            var partitionName = Path.GetFileName(filename);
            AvailablePartitionNames = [partitionName];

            //(The header used to be parsed here too, bitmap and all, only to produce a length hint that was the
            //partition size rather than the image length - gh review F8. PartcloneStream parses it once.)
            var partcloneStream = File.OpenRead(filename);

            Partitions = [];

            if (partitionsToLoad.Count == 0 || partitionsToLoad.Contains(partitionName))
            {
                var partition = new PartclonePartition(
                    filename,
                    this,
                    partitionName,
                    partcloneStream,
                    Compression.None,
                    null,
                    willPerformRandomSeeking,
                    processTrailingNulls);

                Partitions.Add(partition);
            };
        }

        string? containerName;
        public override string ContainerName
        {
            get
            {
                containerName ??= libClonezilla.Utility.ContainerNameFromPath(Filename);

                return containerName;
            }

            protected set
            {
                containerName = value;
            }
        }
    }
}
