using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class RawImage : PartitionContainer
    {
        public RawImage(string filename, List<string> partitionsToLoad, string containerName, bool willPerformRandomSeeking)
        {
            PartitionsToLoad = partitionsToLoad;
            ContainerName = containerName;

            var archiveEntries = SevenZipUtility.GetArchiveEntries(
                                                    filename,
                                                    false,
                                                    true);

            var rawImageStream = File.OpenRead(filename);
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking);
        }

        public RawImage(Stream rawImageStream, List<string> partitionsToLoad, string containerName, IEnumerable<ArchiveEntry> archiveEntries, bool willPerformRandomSeeking)
        {
            PartitionsToLoad = partitionsToLoad;
            ContainerName = containerName;
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking);
        }

        public void SetupFromStream(Stream rawImageStream, IEnumerable<ArchiveEntry> archiveEntries, bool willPerformRandomSeeking)
        {
            var firstArchiveEntry = archiveEntries.FirstOrDefault();

            //we have to work out if this is a drive image, or a partition image

            var isDriveImage = firstArchiveEntry != null && !firstArchiveEntry.IsFolder && Path.GetFileNameWithoutExtension(firstArchiveEntry.Path).Equals("0") && firstArchiveEntry.Offset != null;

            PartitionContainer container;
            if (isDriveImage)
            {
                var partitionImageFiles = archiveEntries.ToList();

                container = new RawDriveImage(ContainerName, PartitionsToLoad, rawImageStream, partitionImageFiles);
            }
            else
            {
                container = new RawPartitionImage(ContainerName, PartitionsToLoad, "partition0", rawImageStream);
            }

            Partitions = container.Partitions;
        }

        public List<string> PartitionsToLoad { get; }
        public override string ContainerName { get; protected set; }
    }
}
