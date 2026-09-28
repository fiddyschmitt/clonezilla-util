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
        //Non-readonly so it can be released once BitmapContentMap has packed it into 64-bit words (it copies,
        //never retains the raw array) - ~64 MiB per 2 TB partition that would otherwise sit on the LOH for the mount's life.
        byte[]? Bitmap;

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
                //The fixed-size header in one read, parsed from memory, so its CRC is checked over exactly the bytes
                //on disk without seeking back (the stream may be a sequential decoder, where that means a restart).
                var headerBytes = binaryReader.ReadBytes(ImageDescV2.Size);
                ImageDescV2 = new ImageDescV2(new BinaryReader(new MemoryStream(headerBytes)));

                var prefix = $"[{containerName}] [{partitionName}]";
                var crcIsVerifiable = ImageDescV2.ImageOptionsV2?.ChecksumMode == CrcModeEnum.CSM_CRC32;
                ValidateV2Header(prefix, ImageDescV2, headerBytes, crcIsVerifiable);

                var v2BitmapMode = ImageDescV2.ImageOptionsV2?.BitmapMode ?? BitmapMode.BM_NONE;
                if (ImageDescV2.FileSystemInfoV2 != null)
                {
                    //BM_BYTE stores a byte per block; everything else a bit per block, ceil(totalBlocks / 8). This used
                    //to assume bits for every mode, so a BM_BYTE image read an eighth of its bitmap and then treated the
                    //rest as block content (gh review F33). Divide before the cast to avoid overflow on huge partitions.
                    var totalBlocksV2 = ImageDescV2.FileSystemInfoV2.TotalBlocks;
                    var bytesUsedByBitmapLong = v2BitmapMode == BitmapMode.BM_BYTE ? totalBlocksV2 : (totalBlocksV2 + 7) / 8;
                    if (bytesUsedByBitmapLong > (ulong)Array.MaxLength)
                        throw new NotSupportedException($"{prefix} The partclone bitmap ({bytesUsedByBitmapLong:N0} bytes) is too large to load.");

                    Bitmap = binaryReader.ReadBytes((int)bytesUsedByBitmapLong);
                    if (Bitmap.Length != (int)bytesUsedByBitmapLong)
                        throw new InvalidDataException($"{prefix} The partclone image ends inside its bitmap; it is truncated.");

                    var storedBitmapCrc = binaryReader.ReadUInt32();
                    CheckCrc(prefix, "bitmap", PartcloneCrc32.Compute(Bitmap), storedBitmapCrc, crcIsVerifiable);
                }

                bitmapMode = v2BitmapMode;
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
                  //Debug (not Information): since the arithmetic BitmapContentMap (Batch 5) this is near-instant,
                  //so it no longer warrants a console/log line - kept as a breadcrumb for the debugger output.
                  Log.Debug($"[{containerName}] [{partitionName}] Building partclone content map");

                  if (Bitmap == null) throw new Exception($"[{containerName}] [{partitionName}] BitMap is not populated.");

                  var map = new BitmapContentMap(Bitmap, bitmapMode, blockSize, checksumSize, blocksPerChecksum, StartOfContent, totalBlocks, Length);
                  Bitmap = null;   //packed into the map (which copies, not retains); drop the raw array
                  return map;
              });
        }

        //A corrupt or foreign header used to surface much later, as a nonsensical length or a content-map failure
        //(gh review F43). Checked here instead: the endianness marker (this parser reads little-endian images only),
        //then the header CRC.
        static void ValidateV2Header(string prefix, ImageDescV2 desc, byte[] headerBytes, bool crcIsVerifiable)
        {
            if (headerBytes.Length < ImageDescV2.Size)
                throw new InvalidDataException($"{prefix} The partclone image ends inside its header; it is truncated.");

            var marker = desc.ImageHeadV2?.EndiannessMarker ?? 0;
            if (marker == ImageHeadV2.BigEndianMarker)
                throw new NotSupportedException($"{prefix} This partclone image was written on a big-endian machine, which is not supported.");
            if (marker != ImageHeadV2.LittleEndianMarker)
                throw new InvalidDataException($"{prefix} Not a valid partclone v2 header (endianness marker 0x{marker:X4}, expected 0x{ImageHeadV2.LittleEndianMarker:X4}); the image is corrupt.");

            CheckCrc(prefix, "header", PartcloneCrc32.Compute(headerBytes.AsSpan(0, ImageDescV2.CrcCoveredBytes)), desc.CRC, crcIsVerifiable);
        }

        //A mismatch is fatal for images in CRC-32 checksum mode, the variant verified against real images. For any
        //other mode it is only a warning, in case partclone computes these CRCs differently there.
        static void CheckCrc(string prefix, string what, uint computed, uint stored, bool fatal)
        {
            if (computed == stored) return;

            var message = $"{prefix} The partclone {what} CRC does not match (stored 0x{stored:X8}, computed 0x{computed:X8}); the image is corrupt.";
            if (fatal) throw new InvalidDataException(message);
            Log.Warning(message);
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
