using lib7Zip;
using libClonezilla.Decompressors;
using libCommon;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using libPartclone;
using Serilog;
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
        public CompressedImage(string filename, List<string> partitionsToLoad, bool willPerformRandomSeeking, Folder tempFolder, bool processTrailingNulls)
        {
            ContainerName = Path.GetFileNameWithoutExtension(filename);

            var currentFilename = filename;
            string? wholeFileCacheFolder = null;

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
                    decompressedStream = new PartcloneStream("", "", streamToInspect);
                    //keep the previous compression layer's identity folder: the partclone-decoded
                    //content is a pure function of that layer's content, so the key stays stable
                    //and unique - and nulling it here cost bare-partclone mounts their toplevel and
                    //partition caches on every open (TEST_ANALYSIS.md #35, Lead L10)
                }
                else
                {
                    var decompressorSelector = new DecompressorSelector(filename, ContainerName, streamToInspect, null, compression, null, processTrailingNulls);
                    decompressedStream = decompressorSelector.GetSeekableStream();

                    //identity folder of this decompressed content - it lets the partitions inside
                    //the image have real caches (file lists, serving decisions) even though a bare
                    //image file has no clonezilla-style cache folder. Memoized by the selector, so
                    //this is free when GetSeekableStream already computed it.
                    try
                    {
                        wholeFileCacheFolder = decompressorSelector.GetWholeFileCacheFolder();
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[{ContainerName}] No whole-file cache folder available ({ex.Message}).");
                        wholeFileCacheFolder = null;
                    }
                }

                var tempName = Path.GetFileNameWithoutExtension(Path.GetFileName(TempUtility.GetTempFilename(false)));
                var virtualDecompressedFile = new StreamBackedFileEntry(tempName, tempFolder, decompressedStream);

                currentFilename = virtualDecompressedFile.FullPath;
            }

            var container = new RawImage(currentFilename, partitionsToLoad, ContainerName, willPerformRandomSeeking, processTrailingNulls, wholeFileCacheFolder);

            Partitions = container.Partitions;
            AvailablePartitionNames = container.AvailablePartitionNames;
        }

        public override string ContainerName { get; protected set; }
    }
}
