using lib7Zip;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using MountDocushare.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers
{
    public class ImageFile : PartitionContainer
    {
        public ImageFile(string filename, bool willPerformRandomSeeking)
        {
            ImageFilename = filename;

            var partitionName = Path.GetFileName(filename);

            Stream mainFileStream = File.OpenRead(filename);


            var compressionInUse = IDecompressor.GetCompressionType(mainFileStream);

            //we have to work out if this file is a drive image, or a partition image

            //grab the very first archive entry, then stop the process
            ArchiveEntry? firstArchiveEntry = null;
            var archiveEntries = SevenZipUtility.GetArchiveEntries(
                                                    filename,
                                                    false,
                                                    true,
                                                    () => firstArchiveEntry != null);

            foreach (var entry in archiveEntries)
            {
                firstArchiveEntry = entry;
                break;
            }

            if (firstArchiveEntry == null) throw new Exception($"Could not retrieve the first entry from: {filename}");

            if (!firstArchiveEntry.IsFolder && Path.GetFileNameWithoutExtension(firstArchiveEntry.Path).Equals("0") && firstArchiveEntry.Offset != null)
            {
                //this is a drive image, containing 1 or more partitions

                var partitionImageFiles = SevenZipUtility.GetArchiveEntries(filename, false, true).ToList();

                //protect this stream from concurrent access
                mainFileStream = Stream.Synchronized(mainFileStream);

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

                                    var partitionStream = new SubStream(mainFileStream, partitionStart, partitionEnd);

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
            else
            {
                //this is a single-partition image

                var partition = new ImageFilePartition(this, partitionName, mainFileStream, mainFileStream.Length, Compression.None, null, true);

                Partitions = new()
                {
                    partition
                };
            }
        }

        string? containerName;
        public override string Name
        {
            get
            {
                if (containerName == null)
                {
                    containerName = Path.GetFileNameWithoutExtension(ImageFilename);
                }

                return containerName;
            }

            set
            {
                containerName = value;
            }
        }

        public string ImageFilename { get; }
    }
}
