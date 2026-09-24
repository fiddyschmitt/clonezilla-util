using lib7Zip;
using libClonezilla.Cache;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libCommon;
using libCommon.Streams;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class RawDriveImage : PartitionContainer
    {
        public RawDriveImage(string containerName, List<string> partitionsToLoad, Stream rawDriveStream, List<ArchiveEntry> partitionImageFiles, bool processTrailingNulls, string? wholeFileCacheFolder)
        {
            ContainerName = containerName;
            SetupFromStream(rawDriveStream, partitionImageFiles, partitionsToLoad, processTrailingNulls, wholeFileCacheFolder);
        }

        /// <summary>The leading integer index 7-Zip's MBR/GPT partition-table handlers put before the
        /// first dot of a partition entry's name ("0.ntfs" -> "0", "0.EFI system partition.fat" -> "0"),
        /// or null when the entry is not a partition-table entry: a folder, one with no disk Offset, or
        /// one whose leading token is not an integer (i.e. an ordinary filesystem file). This is the
        /// signature that tells a whole-drive image apart from a single-partition image.</summary>
        internal static string? PartitionTableIndex(ArchiveEntry e)
        {
            if (e.IsFolder || e.Offset == null) return null;

            var name = Path.GetFileName(e.Path);
            if (string.IsNullOrEmpty(name)) return null;

            var firstDot = name.IndexOf('.');
            var token = firstDot >= 0 ? name[..firstDot] : name;
            return token.Length > 0 && token.All(char.IsAsciiDigit) ? token : null;
        }

        void SetupFromStream(Stream rawDriveStream, List<ArchiveEntry> partitionImageFiles, List<string> partitionsToLoad, bool processTrailingNulls, string? wholeFileCacheFolder)
        {
            if (partitionImageFiles.Count == 0)
            {
                Log.Information($"No partitions to process.");
                return;
            }

            var sharedDriveStream = new SharedStream(rawDriveStream);

            var partitionInfos = partitionImageFiles
                            .Where(partitionImageFile => partitionImageFile.Offset != null)
                            .Select(partitionImageFile =>
                            {
                                var startByte = partitionImageFile.Offset!.Value;
                                var length = partitionImageFile.Size;
                                var endByte = startByte + length;

                                //name by the partition-table index only (never the GPT label): "0.EFI
                                //system partition.fat" must yield "partition0", not "partition0.EFI system
                                //partition" (gh review F2). RawImage already validated every entry here.
                                var index = PartitionTableIndex(partitionImageFile)
                                            ?? Path.GetFileNameWithoutExtension(partitionImageFile.Path);

                                return new
                                {
                                    PartitionFilename = partitionImageFile,
                                    PartitionName = $"partition{index}",
                                    StartByte = startByte,
                                    PartitionLength = length,
                                    EndByte = endByte
                                };
                            })
                            .ToList();

            AvailablePartitionNames = partitionInfos.Select(partitionInfo => partitionInfo.PartitionName).ToList();

            Partitions = partitionInfos
                            .Where(partitionInfo => partitionsToLoad.Count == 0 || partitionsToLoad.Contains(partitionInfo.PartitionName))
                            .Select(partitionInfo =>
                            {
                                //The strange thing is that partitionEnd can be beyond the length of the original file.
                                //In the case of the sda2.img test file, it is 868,352 bytes beyond the end.
                                //This is enough for 7-Zip to conclude it isn't an archive.
                                //To address this, we ask SubStream to serve (null) bytes beyond the original bytes.

                                //have to give a clean stream (own cursor) to the SubStream, otherwise multiple readers can interfere with each other. (Not possible to address using Stream.Synchronized because any position tracking is thwarted by the fact that Seek and Read can be called by another thread )

                                var partitionStream = new SubStream(sharedDriveStream.CreateView(), partitionInfo.StartByte, partitionInfo.EndByte);

                                //Stream stream = new SeekableStreamUsingRestarts(() =>
                                //{
                                //    //Unfortunately, this is not fast enough because when listing the files in sda.img\1.ntfs, 7-Zip asks us to seek from 3G to 12GB position, which takes longer than 20 seconds.
                                //    //We need a faster way of seeking inside drive images.
                                //    var stream = SevenZipUtility.ExtractFileFromArchive(filename, partitionImageFile.Path);

                                //    //Don't use this, because it'll just read until the process is complete (generating a huge temp file).
                                //    //Consider adding a feature to SiphonStream where it only uses the pump when in WaitForPositionToBeAvailable()
                                //    /*
                                //    var tempFilename = TempUtility.GetTempFilename(true);
                                //    var tempFileStream = new FileStream(tempFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
                                //    result = new SiphonStream(result, tempFileStream);
                                //    */

                                //    //result = new PositionTrackerStream(result);

                                //    return stream;
                                //}, partitionImageFile.Size);

                                //var totalSystemRAMInBytes = libCommon.Utility.GetTotalRamSizeBytes();
                                //var totalSystemRAMInMegabytes = (int)(totalSystemRAMInBytes / (double)(1024 * 1024));
                                //var maxCacheSizeInMegabytes = totalSystemRAMInMegabytes / 4;

                                //stream = new CachingStream(stream, null, EnumCacheType.LimitByRAMUsage, maxCacheSizeInMegabytes, null);

                                //partitions inside a bare image file have no clonezilla-style cache
                                //folder; the image's whole-file identity folder stands in, so they
                                //still get cached file lists and serving decisions (TEST_ANALYSIS.md #5)
                                IPartitionCache? partitionCache = null;
                                if (wholeFileCacheFolder != null)
                                {
                                    partitionCache = new PartitionCache(wholeFileCacheFolder, partitionInfo.PartitionName);
                                }

                                Partition partition = new ImageFilePartition(
                                    partitionInfo.PartitionFilename.Path,
                                    this,
                                    partitionInfo.PartitionName,
                                    partitionStream,
                                    partitionInfo.PartitionLength,
                                    Compression.None,
                                    partitionCache,
                                    true,
                                    processTrailingNulls);
                                return partition;
                            })
                            .ToList();
        }

        public override string ContainerName { get; protected set; }
    }
}
