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
        public RawImage(string filename, string containerName, bool willPerformRandomSeeking)
        {
            ContainerName = containerName;

            var archiveEntries = SevenZipUtility.GetArchiveEntries(
                                                    filename,
                                                    false,
                                                    true);

            var rawImageStream = File.OpenRead(filename);
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking);
        }

        public RawImage(Stream rawImageStream, string containerName, IEnumerable<ArchiveEntry> archiveEntries, bool willPerformRandomSeeking)
        {
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

                container = new RawDriveImage(ContainerName, rawImageStream, partitionImageFiles);
            }
            else
            {
                container = new RawPartitionImage(ContainerName, "partition0", rawImageStream);
            }

            Partitions = container.Partitions;
        }

        public override string ContainerName { get; protected set; }
    }
}
