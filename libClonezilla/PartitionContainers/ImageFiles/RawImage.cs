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
        public RawImage(string filename, List<string> partitionsToLoad, string containerName, bool willPerformRandomSeeking, bool processTrailingNulls)
        {
            Filename = filename;
            PartitionsToLoad = partitionsToLoad;
            ContainerName = containerName;

            var archiveEntries = SevenZipUtility.GetArchiveEntries(
                                                    filename,
                                                    false,
                                                    true);

            var rawImageStream = File.OpenRead(filename);
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking, processTrailingNulls);
        }

        public RawImage(
            string filename, 
            Stream rawImageStream, 
            List<string> partitionsToLoad, 
            string containerName, 
            IEnumerable<ArchiveEntry> archiveEntries, 
            bool willPerformRandomSeeking,
            bool processTrailingNulls)
        {
            Filename = filename;
            PartitionsToLoad = partitionsToLoad;
            ContainerName = containerName;
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking, processTrailingNulls);
        }

        public void SetupFromStream(Stream rawImageStream, IEnumerable<ArchiveEntry> archiveEntries, bool willPerformRandomSeeking, bool processTrailingNulls)
        {
            var firstArchiveEntry = archiveEntries.FirstOrDefault();

            //we have to work out if this is a drive image, or a partition image

            var isDriveImage = firstArchiveEntry != null && !firstArchiveEntry.IsFolder && Path.GetFileNameWithoutExtension(firstArchiveEntry.Path).Equals("0") && firstArchiveEntry.Offset != null;

            PartitionContainer container;
            if (isDriveImage)
            {
                var partitionImageFiles = archiveEntries.ToList();

                container = new RawDriveImage(ContainerName, PartitionsToLoad, rawImageStream, partitionImageFiles, processTrailingNulls);
            }
            else
            {
                container = new RawPartitionImage(Filename, ContainerName, PartitionsToLoad, "partition0", rawImageStream, processTrailingNulls);
            }

            Partitions = container.Partitions;
        }

        public string Filename { get; }
        public List<string> PartitionsToLoad { get; }
        public override string ContainerName { get; protected set; }
    }
}
