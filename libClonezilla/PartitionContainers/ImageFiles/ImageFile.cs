using lib7Zip;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libClonezilla.VFS;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using libPartclone;
using MountDocushare.Streams;
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
        public ImageFile(string filename, List<string> partitionsToLoad, bool willPerformRandomSeeking, IVFS vfs)
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
                container = new PartcloneFile(filename, partitionsToLoad, willPerformRandomSeeking);
            }
            else
            {
                var compressionInUse = Decompressor.GetCompressionType(mainFileStream);

                if (compressionInUse == Compression.None)
                {
                    container = new RawImage(filename, partitionsToLoad, ContainerName, willPerformRandomSeeking);
                }
                else
                {
                    //To inspect compressed images, we need a virtual temp folder.
                    //Let's get one from the VFS.
                    var tempFolder = vfs.CreateTempFolder();
                    container = new CompressedImage(filename, partitionsToLoad, willPerformRandomSeeking, tempFolder);
                }
            }

            Partitions = container.Partitions;
        }

        public override string ContainerName { get; protected set; }
    }
}
