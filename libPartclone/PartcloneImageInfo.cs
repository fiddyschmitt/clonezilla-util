using libPartclone.Metadata;
using Serilog;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone
{
    public class PartcloneImageInfo
    {
        public ImageDescV1? ImageDescV1;
        public ImageDescV2? ImageDescV2;

        //Each bit in this bitmap represents a block in the original partition. If the bit is 0, then an empty block should be created. If bit is 1, then the original block should be restored.
        readonly byte[]? Bitmap;

        //Format parameters, resolved once from the V1/V2 header and handed to the content map.
        readonly BitmapMode bitmapMode;
        readonly uint blockSize;
        readonly ushort checksumSize;
        readonly uint blocksPerChecksum;   //0 => image stores no checksums
        readonly ulong totalBlocks;

        public long StartOfContent { get; }
        public string ContainerName { get; }
        public string PartitionName { get; }
        public Stream? ReadStream { get; set; }

        //Partclone images only store blocks from the original file if they were populated. A block is a range of bytes (eg. 4096 bytes)
        //If the original file contains:    <populated block 1><populated block 2><UNPOPULATED><populated block 3>
        //Then partclone will store:        <populated block 1><populated block 2><populated block 3>

        //Every n populated blocks, a checksum is stored.
        //So the partclone content will look like this: <populated block 1><populated block 2><populated block n><checksum><populated block n+1>

        //So when we are restoring the original file from the partclone contents, we have to skip checksums and restore both populated and unpopulated blocks.

        //The content map resolves an output position to the corresponding section in the partclone content,
        //arithmetically from the bitmap (see BitmapContentMap). Built lazily so that callers who only want
        //header info (e.g. Length) don't pay to build it.
        readonly Lazy<IPartcloneContentMap> contentMapLazy;
        public IPartcloneContentMap ContentMap => contentMapLazy.Value;

        public PartcloneImageInfo(string containerName, string partitionName, Stream readStream)
        {
            ContainerName = containerName;
            PartitionName = partitionName;
            ReadStream = readStream;

            using var binaryReader = new BinaryReader(readStream, new UTF8Encoding(), true);

            //find the image version
            var currentPos = binaryReader.BaseStream.Position;
            binaryReader.BaseStream.Seek(30, SeekOrigin.Current);
            var imageVersion = Encoding.ASCII.GetString(binaryReader.ReadBytes(4)).TrimEnd('\0');
            binaryReader.BaseStream.Seek(currentPos, SeekOrigin.Begin);

            if (imageVersion.Equals("0001"))
            {
                ImageDescV1 = new ImageDescV1(binaryReader);

                //this is what's done in partclone.c.
                // read the extra bytes
                //binaryReader.BaseStream.Seek(0x1C4AF, SeekOrigin.Begin);

                if (ImageDescV1.FileSystemInfoV1 != null)
                {
                    var bytesUsedByBitmap = (int)ImageDescV1.FileSystemInfoV1.TotalBlocks;
                    Bitmap = binaryReader.ReadBytes(bytesUsedByBitmap);
                }

                binaryReader.ReadBytes(8);  //BIT_MAGIC_SIZE = 8. This is just a magic string to signify the start of the Bitmap. BiTmAgIc

                //ImageDescV1 always uses BM_BYTE, a 4-byte checksum and 1 block per checksum.
                //See partclone.c "void set_image_options_v1".
                bitmapMode = BitmapMode.BM_BYTE;
                blockSize = ImageDescV1.FileSystemInfoV1?.BlockSize ?? throw new Exception("Could not retrieve block size");
                checksumSize = 4;
                blocksPerChecksum = 1;
                totalBlocks = ImageDescV1.FileSystemInfoV1?.TotalBlocks ?? 0;
            }
            else if (imageVersion.Equals("0002"))
            {
                ImageDescV2 = new ImageDescV2(binaryReader);

                if (ImageDescV2.FileSystemInfoV2 != null)
                {
                    //ceil(totalBlocks / 8); divide before the int cast to avoid overflow on huge partitions
                    var bytesUsedByBitmap = (int)((ImageDescV2.FileSystemInfoV2.TotalBlocks + 7) / 8);

                    Bitmap = binaryReader.ReadBytes(bytesUsedByBitmap);

                    binaryReader.ReadBytes(4);
                }

                bitmapMode = ImageDescV2.ImageOptionsV2?.BitmapMode ?? BitmapMode.BM_NONE;
                blockSize = ImageDescV2.FileSystemInfoV2?.BlockSize ?? throw new Exception("Could not retrieve block size");
                checksumSize = ImageDescV2.ImageOptionsV2?.ChecksumSize ?? 4;
                blocksPerChecksum = ImageDescV2.ImageOptionsV2?.BlocksPerChecksum ?? 1;
                totalBlocks = ImageDescV2.FileSystemInfoV2?.TotalBlocks ?? 0;
            }
            else
            {
                throw new Exception($"Unknown partclone image version: {imageVersion}");
            }

            StartOfContent = binaryReader.BaseStream.Position;

            if (ImageDescV1 != null) Log.Debug(ImageDescV1.ToString());
            if (ImageDescV2 != null) Log.Debug(ImageDescV2.ToString());

            contentMapLazy = new Lazy<IPartcloneContentMap>(() =>
              {
                  Log.Information($"[{containerName}] [{partitionName}] Building partclone content map");

                  if (Bitmap == null) throw new Exception($"[{containerName}] [{partitionName}] BitMap is not populated.");

                  return new BitmapContentMap(Bitmap, bitmapMode, blockSize, checksumSize, blocksPerChecksum, StartOfContent, totalBlocks, Length);
              });
        }

        public long Length
        {
            get
            {
                long deviceSizeBytes = (long)(ImageDescV1?.FileSystemInfoV1?.DeviceSizeBytes ?? ImageDescV2?.FileSystemInfoV2?.DeviceSizeBytes ?? 0);
                return deviceSizeBytes;
            }
        }

        public static bool IsPartclone(Stream? stream)
        {
            if (stream == null) return false;

            var binaryReader = new BinaryReader(stream);
            var magic = Encoding.ASCII.GetString(binaryReader.ReadBytes(16)).TrimEnd('\0');

            var result = magic.Equals("partclone-image");
            return result;
        }
    }
}
