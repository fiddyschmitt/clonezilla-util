using lib7Zip;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libCommon;
using libCommon.Streams;
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
        public RawDriveImage(string containerName, string filename)
        {
            ContainerName = containerName;

            var partitionImageFiles = SevenZipUtility.GetArchiveEntries(filename, false, true).ToList();

            var rawDriveStream = File.OpenRead(filename);

            SetupFromStream(rawDriveStream, partitionImageFiles);
        }

        public RawDriveImage(string containerName, Stream rawDriveStream, List<ArchiveEntry> partitionImageFiles)
        {
            ContainerName = containerName;
            SetupFromStream(rawDriveStream, partitionImageFiles);
        }

        void SetupFromStream(Stream rawDriveStream, List<ArchiveEntry> partitionImageFiles)
        {
            var readLock = new object();

            Partitions = partitionImageFiles
                            .Where(partitionImageFile => partitionImageFile.Offset != null)
                            .Sandwich()
                            .Select(partitionImageFile =>
                            {
                                var partitionName = $"partition{Path.GetFileNameWithoutExtension(partitionImageFile.Current.Path)}";

                                var partitionStart = partitionImageFile.Current.Offset!.Value;
                                var partitionLength = partitionImageFile.Current.Size;
                                var partitionEnd = partitionStart + partitionLength;

                                //The strange thing is that partitionEnd can be beyond the length of the original file.
                                //In the case of the sda2.img test file, it is 868,352 bytes beyond the end.
                                //This is enough for 7-Zip to conclude it isn't an archive.
                                //To address this, we ask SubStream to serve (null) bytes beyond the original bytes.

                                //have to give a clean stream to the SubStream, otherwise multiple readers can interfere with each other. (Not possible to address using Stream.Synchronised because any position tracking is thwarted by the fact that Seek and Read can be called by another thread )

                                var independentStream = new IndependentStream(rawDriveStream, readLock);
                                var partitionStream = new SubStream(independentStream, partitionStart, partitionEnd);

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

                                Partition partition = new ImageFilePartition(this, partitionName, partitionStream, partitionLength, Compression.None, null, true);
                                return partition;
                            })
                            .ToList();
        }

        public override string ContainerName { get; protected set; }
    }
}
