using lib7Zip;
using libClonezilla.Decompressors;
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
    public class CompressedImage : PartitionContainer
    {
        public CompressedImage(string filename, List<string> partitionsToLoad, bool willPerformRandomSeeking, Folder tempFolder)
        {
            ContainerName = Path.GetFileNameWithoutExtension(filename);

            var currentFilename = filename;

            while (true)
            {
                Stream streamToInspect = File.OpenRead(currentFilename);

                //protect this stream from concurrent access
                streamToInspect = Stream.Synchronized(streamToInspect);

                var isPartcloneStream = PartcloneImageInfo.IsPartclone(streamToInspect);

                var compression = Decompressor.GetCompressionType(streamToInspect);

                if (compression == Compression.None && !isPartcloneStream)
                {
                    //finally dealing with uncompressed content
                    break;
                }

                Stream decompressedStream;

                if (isPartcloneStream)
                {
                    decompressedStream = new PartcloneStream("", "", streamToInspect, null);
                }
                else
                {
                    var decompressorSelector = new DecompressorSelector(filename, ContainerName, streamToInspect, null, compression, null);
                    decompressedStream = decompressorSelector.GetSeekableStream();
                }

                var virtualDecompressedFile = new StreamBackedFileEntry(Guid.NewGuid().ToString(), tempFolder, decompressedStream);

                currentFilename = virtualDecompressedFile.FullPath;
            }

            var container = new RawImage(currentFilename, partitionsToLoad, ContainerName, willPerformRandomSeeking);

            Partitions = container.Partitions;
        }

        public override string ContainerName { get; protected set; }
    }
}
