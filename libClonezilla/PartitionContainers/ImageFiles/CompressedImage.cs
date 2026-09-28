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
        public CompressedImage(string filename, List<string> partitionsToLoad, bool willPerformRandomSeeking, bool processTrailingNulls)
        {
            ContainerName = Path.GetFileNameWithoutExtension(filename);

            //Peel the layers in-process: each decompressed (or partclone-decoded) stream is the input of the next
            //inspection, and the innermost is handed to RawImage as a stream. Every layer used to be published as a
            //hidden file on a Dokan temp mount and re-opened through the mount by name, so the headless verbs needed
            //Dokan for compressed image files, and the exe read its own mount in-process (gh review F46).
            Stream current = Stream.Synchronized(File.OpenRead(filename));   //protect the file stream from concurrent access
            string? wholeFileCacheFolder = null;

            while (true)
            {
                current.Seek(0, SeekOrigin.Begin);   //IsPartclone reads from the current position
                var isPartcloneStream = PartcloneImageInfo.IsPartclone(current);

                var compression = Decompressor.GetCompressionType(current);

                if (compression == Compression.None && !isPartcloneStream)
                {
                    //finally dealing with uncompressed content
                    break;
                }

                Stream decompressedStream;

                if (isPartcloneStream)
                {
                    decompressedStream = new PartcloneStream("", "", current);
                    //keep the previous compression layer's identity folder: the partclone-decoded
                    //content is a pure function of that layer's content, so the key stays stable
                    //and unique - and nulling it here cost bare-partclone mounts their toplevel and
                    //partition caches on every open (TEST_ANALYSIS.md #35, Lead L10)
                }
                else
                {
                    var decompressorSelector = new DecompressorSelector(filename, ContainerName, current, null, compression, null, processTrailingNulls);
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

                current = decompressedStream;
            }

            var container = new RawImage(current, filename, partitionsToLoad, ContainerName, willPerformRandomSeeking, processTrailingNulls, wholeFileCacheFolder);

            Partitions = container.Partitions;
            AvailablePartitionNames = container.AvailablePartitionNames;
        }

        public override string ContainerName { get; protected set; }
    }
}
