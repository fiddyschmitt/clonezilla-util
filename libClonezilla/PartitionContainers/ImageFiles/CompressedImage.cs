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
        public CompressedImage(string filename, bool willPerformRandomSeeking, Folder tempFolder)
        {
            ContainerName = Path.GetFileNameWithoutExtension(filename);

            var currentFilename = filename;

            while (true)
            {
                Stream streamToInspect = File.OpenRead(currentFilename);

                //protect this stream from concurrent access
                streamToInspect = Stream.Synchronized(streamToInspect);

                var compression = IDecompressor.GetCompressionType(streamToInspect);

                if (compression == Compression.None)
                {
                    //finally dealing with uncompressed content
                    break;
                }

                var decompressorSelector = new DecompressorSelector(ContainerName, streamToInspect, null, compression, null);
                var decompressedStream = decompressorSelector.GetSeekableStream();

                var virtualDecompressedFile = new StreamBackedFileEntry(Guid.NewGuid().ToString(), tempFolder, decompressedStream);

                currentFilename = virtualDecompressedFile.FullPath;
            }

            var container = new RawImage(currentFilename, ContainerName, willPerformRandomSeeking);

            Partitions = container.Partitions;
        }

        public override string ContainerName { get; protected set; }
    }
}
