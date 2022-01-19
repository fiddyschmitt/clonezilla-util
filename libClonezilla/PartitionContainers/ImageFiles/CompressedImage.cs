using lib7Zip;
using libClonezilla.Decompressors;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class CompressedImage : PartitionContainer
    {
        public CompressedImage(string filename, Compression compressionInUse, bool willPerformRandomSeeking)
        {
            ContainerName = Path.GetFileNameWithoutExtension(filename);

            Stream compressedStream = File.OpenRead(filename);

            //protect this stream from concurrent access
            compressedStream = Stream.Synchronized(compressedStream);

            //this is a compressed drive image, let's decompress it and work out what to do
            var decompressorSelector = new DecompressorSelector(ContainerName, compressedStream, null, compressionInUse, null);

            //we have to mount the uncompressed file, so we can inspect its contents
            var tempContainer = new RawImage(filename, ContainerName, willPerformRandomSeeking);
            var tempMountPoint = libDokan.Utility.GetAvailableDriveLetter();

            //todo: just pass in a container root from the very top
            var root = new Folder("", null);
            var containerRoot = new Folder("image", root)
            {
                Hidden = true
            };

            var decompressedStream = decompressorSelector.GetSeekableStream();
            var virtualDecompressedFile = new StreamBackedFileEntry(Path.GetFileName(filename), containerRoot, () =>
            {
                return decompressedStream;
            });

            Utility.MountPartitionsAsImageFiles(
                "",
                tempContainer,
                tempMountPoint,
                containerRoot,
                root);

            //get the list of files inside this
            var physicalDecompressedFile = Path.Combine(tempMountPoint, virtualDecompressedFile.FullPath);

            //we now have the uncompressed image file
            var container = new RawImage(physicalDecompressedFile, ContainerName, willPerformRandomSeeking);

            Partitions = container.Partitions;
        }

        public override string ContainerName { get; protected set; }
    }
}
