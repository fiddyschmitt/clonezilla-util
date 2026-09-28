using lib7Zip;
using lib7Zip.Native;
using Serilog;
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
        public RawImage(string filename, List<string> partitionsToLoad, string containerName, bool willPerformRandomSeeking, bool processTrailingNulls, string? wholeFileCacheFolder)
        {
            Filename = filename;
            PartitionsToLoad = partitionsToLoad;
            ContainerName = containerName;
            WholeFileCacheFolder = wholeFileCacheFolder;

            List<ArchiveEntry> archiveEntries;
            using (var enumStream = File.OpenRead(filename))
            {
                archiveEntries = EnumerateTopLevel(enumStream, wholeFileCacheFolder);
            }

            var rawImageStream = File.OpenRead(filename);
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking, processTrailingNulls);
        }

        /// <summary>
        /// An image that exists only as a stream: the decompressed content of a compressed image file, read
        /// in-process. <paramref name="originName"/> is the file it came from (logs, partition origin). Compressed
        /// image files used to be published as a hidden file on a Dokan temp mount and re-opened through it by name,
        /// so list, extract and extract-partition-image needed Dokan for them (gh review F46).
        /// </summary>
        public RawImage(Stream imageStream, string originName, List<string> partitionsToLoad, string containerName, bool willPerformRandomSeeking, bool processTrailingNulls, string? wholeFileCacheFolder)
        {
            Filename = originName;
            PartitionsToLoad = partitionsToLoad;
            ContainerName = containerName;
            WholeFileCacheFolder = wholeFileCacheFolder;

            var archiveEntries = EnumerateTopLevel(imageStream, wholeFileCacheFolder);

            imageStream.Seek(0, SeekOrigin.Begin);
            SetupFromStream(imageStream, archiveEntries, willPerformRandomSeeking, processTrailingNulls);
        }

        // Lists the top level of the image via the native 7-Zip engine, NON-recursively: for a drive
        // image this yields the partition table (each partition with its byte Offset); for a single
        // partition image it yields the filesystem's files. SetupFromStream uses the first entry to
        // tell the two apart. An unrecognised image yields no entries (treated as a single partition).
        // The result is cached in the whole-file cache folder when one exists: for compressed drive
        // images the scan reads partition-table and filesystem structures through the decompressor,
        // which is expensive to repeat on every open (TEST_ANALYSIS.md #5).
        static List<ArchiveEntry> EnumerateTopLevel(Stream imageStream, string? wholeFileCacheFolder)
        {
            var cacheFilename = wholeFileCacheFolder == null ? null : Path.Combine(wholeFileCacheFolder, "toplevel.json");

            if (cacheFilename != null && File.Exists(cacheFilename))
            {
                try
                {
                    using var fs = File.OpenRead(cacheFilename);
                    var cached = System.Text.Json.JsonSerializer.Deserialize(fs, Cache.ArchiveEntryJsonContext.Default.ListArchiveEntry);
                    if (cached != null)
                    {
                        return cached;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Could not load cached top-level entries from {cacheFilename} ({ex.Message}). Re-enumerating.");
                }
            }

            List<ArchiveEntry> entries;
            try
            {
                imageStream.Seek(0, SeekOrigin.Begin);
                using var arc = new SevenZipNativeArchive(imageStream, SevenZipUtility.SevenZipDll(), ownsStream: false, recursive: false);
                entries = arc.GetEntries()
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
                entries = [];
            }

            if (cacheFilename != null)
            {
                try
                {
                    using var fs = File.Create(cacheFilename);
                    System.Text.Json.JsonSerializer.Serialize(fs, entries, Cache.ArchiveEntryJsonContext.Default.ListArchiveEntry);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Non-fatal. Error while caching top-level entries to {cacheFilename}: {ex.Message}");
                }
            }

            return entries;
        }

        public void SetupFromStream(Stream rawImageStream, IEnumerable<ArchiveEntry> archiveEntries, bool willPerformRandomSeeking, bool processTrailingNulls)
        {
            var entries = archiveEntries.ToList();

            //Distinguish a drive image from a single partition image. A drive image's top-level entries
            //ARE its partitions: each a non-folder blob with a byte Offset into the disk, named with a
            //leading integer index. 7-Zip's MBR handler names them "0.ntfs", "1.ntfs"; its GPT handler
            //adds the (possibly present) partition label - "0.EFI system partition.fat" - so the old test
            //(first entry's name-without-extension equals "0") missed every labelled GPT disk, i.e. every
            //UEFI Windows disk, and served its partition table as raw blobs (gh review F2). A partition
            //image is instead a filesystem, whose top level holds folders/files without disk offsets.
            var isDriveImage = entries.Count > 0 && entries.All(e => RawDriveImage.PartitionTableIndex(e) != null);

            PartitionContainer container;
            if (isDriveImage)
            {
                container = new RawDriveImage(ContainerName, PartitionsToLoad, rawImageStream, entries, processTrailingNulls, WholeFileCacheFolder);
            }
            else
            {
                container = new RawPartitionImage(Filename, ContainerName, PartitionsToLoad, "partition0", rawImageStream, processTrailingNulls, WholeFileCacheFolder);
            }

            Partitions = container.Partitions;
            AvailablePartitionNames = container.AvailablePartitionNames;
        }

        public string Filename { get; }
        public List<string> PartitionsToLoad { get; }
        public string? WholeFileCacheFolder { get; }
        public override string ContainerName { get; protected set; }
    }
}
