using lib7Zip;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libClonezilla.VFS;
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

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class ImageFile : PartitionContainer
    {
        public ImageFile(string filename, bool willPerformRandomSeeking, IVFS vfs)
        {
            Stream mainFileStream = File.OpenRead(filename);

            //protect this stream from concurrent access
            mainFileStream = Stream.Synchronized(mainFileStream);

            ContainerName = Path.GetFileNameWithoutExtension(filename);
            var compressionInUse = IDecompressor.GetCompressionType(mainFileStream);

            //we have to work out if the image file is compressed or not

            PartitionContainer container;

            if (compressionInUse == Compression.None)
            {
                container = new RawImage(filename, ContainerName, willPerformRandomSeeking);
            }
            else
            {
                //To inspect compressed images, we need a virtual temp folder.
                //Let's get one from the VFS.
                var tempFolder = vfs.CreateTempFolder();
                container = new CompressedImage(filename, willPerformRandomSeeking, tempFolder);
            }

            Partitions = container.Partitions;
        }

        public override string ContainerName { get; protected set; }
    }
}
