using lib7Zip;
using libClonezilla.Cache;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libClonezilla.VFS;
using Serilog;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using libPartclone;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class ImageFile : PartitionContainer
    {
        public ImageFile(string filename, List<string> partitionsToLoad, bool willPerformRandomSeeking, Lazy<IVFS> vfs, bool processTrailingNulls)
        {
            Stream mainFileStream = File.OpenRead(filename);

            //protect this stream from concurrent access
            mainFileStream = Stream.Synchronized(mainFileStream);

            ContainerName = Path.GetFileNameWithoutExtension(filename);

            //we have to work out if the image file is compressed or not

            PartitionContainer container;

            var isPartcloneStream = PartcloneImageInfo.IsPartclone(mainFileStream);

            if (isPartcloneStream)
            {
                container = new PartcloneFile(filename, partitionsToLoad, willPerformRandomSeeking, processTrailingNulls);
            }
            else
            {
                var compressionInUse = Decompressor.GetCompressionType(mainFileStream);

                if (compressionInUse == Compression.None)
                {
                    //bare uncompressed image: derive an identity-keyed cache folder from the file
                    //itself, so its partitions still get cached file lists and serving decisions
                    string? wholeFileCacheFolder = null;
                    try
                    {
                        wholeFileCacheFolder = WholeFileCacheManager.GetCacheFolderForFile(filename, ContainerName);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[{ContainerName}] No whole-file cache folder available ({ex.Message}).");
                    }

                    container = new RawImage(filename, partitionsToLoad, ContainerName, willPerformRandomSeeking, processTrailingNulls, wholeFileCacheFolder);
                }
                else
                {
                    //To inspect compressed images, we need a virtual temp folder.
                    //Let's get one from the VFS.
                    var tempFolder = vfs.Value.CreateTempFolder();
                    container = new CompressedImage(filename, partitionsToLoad, willPerformRandomSeeking, tempFolder, processTrailingNulls);
                }
            }

            Partitions = container.Partitions;
            AvailablePartitionNames = container.AvailablePartitionNames;
        }

        public override string ContainerName { get; protected set; }
    }
}
