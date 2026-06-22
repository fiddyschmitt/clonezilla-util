using lib7Zip;
using lib7Zip.Native;
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

            var archiveEntries = EnumerateTopLevel(filename);

            var rawImageStream = File.OpenRead(filename);
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking, processTrailingNulls);
        }

        // Lists the top level of the image via the native 7-Zip engine, NON-recursively: for a drive
        // image this yields the partition table (each partition with its byte Offset); for a single
        // partition image it yields the filesystem's files. SetupFromStream uses the first entry to
        // tell the two apart. An unrecognised image yields no entries (treated as a single partition).
        static List<ArchiveEntry> EnumerateTopLevel(string filename)
        {
            try
            {
                using var enumStream = File.OpenRead(filename);
                using var arc = new SevenZipNativeArchive(enumStream, SevenZipUtility.SevenZipDll(), ownsStream: false, recursive: false);
                return arc.GetEntries()
                            .Select(e => new ArchiveEntry(e.Path)
                            {
                                IsFolder = e.IsDir,
                                Size = e.Size,
                                Offset = e.Offset,
                                Modified = e.Modified ?? default,
                                Created = e.Created ?? default,
                                Accessed = e.Accessed ?? default,
                            })
                            .ToList();
            }
            catch (NotAnArchiveException)
            {
                return [];
            }
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
            AvailablePartitionNames = container.AvailablePartitionNames;
        }

        public string Filename { get; }
        public List<string> PartitionsToLoad { get; }
        public override string ContainerName { get; protected set; }
    }
}
