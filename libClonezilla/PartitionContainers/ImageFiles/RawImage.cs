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

            var archiveEntries = EnumerateTopLevel(filename, wholeFileCacheFolder);

            var rawImageStream = File.OpenRead(filename);
            SetupFromStream(rawImageStream, archiveEntries, willPerformRandomSeeking, processTrailingNulls);
        }

        // Lists the top level of the image via the native 7-Zip engine, NON-recursively: for a drive
        // image this yields the partition table (each partition with its byte Offset); for a single
        // partition image it yields the filesystem's files. SetupFromStream uses the first entry to
        // tell the two apart. An unrecognised image yields no entries (treated as a single partition).
        // The result is cached in the whole-file cache folder when one exists: for compressed drive
        // images the scan reads partition-table and filesystem structures through the decompressor,
        // which is expensive to repeat on every open (TEST_ANALYSIS.md #5).
        static List<ArchiveEntry> EnumerateTopLevel(string filename, string? wholeFileCacheFolder)
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
                using var enumStream = File.OpenRead(filename);
                using var arc = new SevenZipNativeArchive(enumStream, SevenZipUtility.SevenZipDll(), ownsStream: false, recursive: false);
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

        public RawImage(
            string filename,
            Stream rawImageStream,
            List<string> partitionsToLoad,
            string containerName,
            IEnumerable<ArchiveEntry> archiveEntries,
            bool willPerformRandomSeeking,
            bool processTrailingNulls,
            string? wholeFileCacheFolder)
        {
            Filename = filename;
            PartitionsToLoad = partitionsToLoad;
            ContainerName = containerName;
            WholeFileCacheFolder = wholeFileCacheFolder;
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

                container = new RawDriveImage(ContainerName, PartitionsToLoad, rawImageStream, partitionImageFiles, processTrailingNulls, WholeFileCacheFolder);
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
