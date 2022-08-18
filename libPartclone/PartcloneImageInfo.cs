using libPartclone.Cache;
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

        public long StartOfContent { get; }
        public string ContainerName { get; }
        public string PartitionName { get; }
        public Stream? ReadStream { get; set; }
        public IPartcloneCache? Cache { get; }

        //Partclone images only store blocks from the original file if they were populated. A block is a range of bytes (eg. 4096 bytes)
        //If the original file contains:    <populated block 1><populated block 2><UNPOPULATED><populated block 3>
        //Then partclone will store:        <populated block 1><populated block 2><populated block 3>

        //Every n populated blocks, a checksum is stored.
        //So the partclone content will look like this: <populated block 1><populated block 2><populated block n><checksum><populated block n+1>

        //So when we are restoring the original file from the partclone contents, we have to skip checksums and restore both populated and unpopulated blocks.

        //This variable stores a mapping of the output file, to the corresponding section in the partclone content.
        //This way, if we are asked to restore a particular byte, we know where to find it in the partclone content.
        public Lazy<List<ContiguousRange>> PartcloneContentMapping;

        public PartcloneImageInfo(string containerName, string partitionName, Stream readStream, IPartcloneCache? cache)
        {
            ContainerName = containerName;
            PartitionName = partitionName;
            ReadStream = readStream;
            Cache = cache;

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
            }
            else if (imageVersion.Equals("0002"))
            {
                ImageDescV2 = new ImageDescV2(binaryReader);

                if (ImageDescV2.FileSystemInfoV2 != null)
                {
                    //var bytesUsedByBitmap = (int)Math.Ceiling(ImageDescV2.FileSystemInfoV2.TotalBlocks / 8M);
                    var bytesUsedByBitmap = (int)(ImageDescV2.FileSystemInfoV2.TotalBlocks / 8) + 1;
                    Bitmap = binaryReader.ReadBytes(bytesUsedByBitmap);

                    //skip something
                    if (ImageDescV2.FileSystemInfoV2.FileSystemType?.Equals("EXTFS") ?? false)
                    {
                        //Not sure why this is different, or if it's anything related to EXTFS at all.
                        binaryReader.ReadBytes(3);
                    }
                    else
                    {
                        binaryReader.ReadBytes(4);
                    }
                }
            }
            else
            {
                throw new Exception($"Unknown partclone image version: {imageVersion}");
            }

            //File.WriteAllBytes($"C:\\Temp\\{clonezillaArchiveName}\\{partitionName}.bitmap", Bitmap);

            StartOfContent = binaryReader.BaseStream.Position;

            if (ImageDescV1 != null) Log.Debug(ImageDescV1.ToString());
            if (ImageDescV2 != null) Log.Debug(ImageDescV2.ToString());

            var mappingFromCache = cache?.GetPartcloneContentMapping();

            PartcloneContentMapping = new Lazy<List<ContiguousRange>>(() =>
              {
                  if (mappingFromCache == null)
                  {
                      Log.Information($"[{containerName}] [{partitionName}] Reading partclone content map");

                      if (Bitmap == null) throw new Exception($"[{containerName}] [{partitionName}] BitMap is not populated.");

                      BitmapMode bitmapMode = BitmapMode.BM_NONE;
                      if (ImageDescV1 != null) bitmapMode = BitmapMode.BM_BYTE;
                      if (ImageDescV2 != null) bitmapMode = ImageDescV2.ImageOptionsV2?.BitmapMode ?? BitmapMode.BM_NONE;

                      var blockSize = ImageDescV1?.FileSystemInfoV1?.BlockSize ?? ImageDescV2?.FileSystemInfoV2?.BlockSize ?? throw new Exception("Could not retrieve block size");
                      ushort checksumSize = ImageDescV2?.ImageOptionsV2?.ChecksumSize ?? 4;   //ImageDescV1 always uses 4 bytes for the checksum. See partclone.h, or search partclone.c for "void set_image_options_v1"
                      var blocksPerChecksum = ImageDescV2?.ImageOptionsV2?.BlocksPerChecksum ?? 1;   //ImageDescV1 always uses 1 block per checksum. Search partclone.c for "void set_image_options_v1"

                      var partcloneContentMapping = DeduceContiguousRanges(Bitmap, bitmapMode, blockSize, checksumSize, blocksPerChecksum, StartOfContent, Length);
                      cache?.SetPartcloneContentMapping(partcloneContentMapping);

                      return partcloneContentMapping;
                  }
                  else
                  {
                      Log.Debug($"[{containerName}] [{partitionName}] Loading partclone content map from cache");
                      return mappingFromCache;
                  }
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

        static List<ContiguousRange> DeduceContiguousRanges(byte[] Bitmap, BitmapMode bitmapMode, uint blockSize, ushort checksumSize, uint blocksPerChecksum, long startOfContent, long totalLength)
        {
            long blockIndex = 0;
            long populatedBlockIndex = 0;

            IEnumerable<bool>? bmp = null;

            bmp = bitmapMode switch
            {
                BitmapMode.BM_BIT => Bitmap.SelectMany(byt => byt.GetBits().Reverse()),
                BitmapMode.BM_BYTE => Bitmap
                                        .Select(byt => byt != 0x0)
                                        .Concat(new[] { false }),
                _ => throw new Exception($"Unsupported BitmapMode: {bitmapMode}"),
            };

            ContiguousRange? currentRange = null;
            ContiguousRange? lastPopulatedRange = null;

            var result = new List<ContiguousRange>();

            foreach (bool blockIsPopulated in bmp)
            {
                var differentFromPreviousRange = (currentRange is null) || currentRange?.IsPopulated != blockIsPopulated;
                var isPrecededByChecksum = (populatedBlockIndex > 0) && (populatedBlockIndex % blocksPerChecksum == 0);

                if (currentRange == null || differentFromPreviousRange || isPrecededByChecksum)
                {
                    var contentRange = new ByteRange()
                    {
                        StartByte = blockIndex * blockSize
                    };
                    ByteRange? partcloneContentRange = null;

                    if (blockIsPopulated)
                    {
                        partcloneContentRange = new ByteRange();

                        if (lastPopulatedRange == null)
                        {
                            partcloneContentRange.StartByte = startOfContent;
                        }
                        else
                        {
                            if (lastPopulatedRange.PartcloneContentRange != null)
                            {
                                partcloneContentRange.StartByte = lastPopulatedRange.PartcloneContentRange.EndByte + 1;
                            }
                        }

                        if (isPrecededByChecksum)
                        {
                            partcloneContentRange.StartByte += checksumSize;
                        }
                    }

                    var newContiguousRange = new ContiguousRange(partcloneContentRange, contentRange);

                    result.Add(newContiguousRange);
                    currentRange = newContiguousRange;
                    if (newContiguousRange.IsPopulated)
                    {
                        lastPopulatedRange = newContiguousRange;
                    }
                }

                currentRange.OutputFileRange.EndByte = ((blockIndex + 1) * blockSize) - 1;
                blockIndex++;

                if (blockIsPopulated && currentRange.PartcloneContentRange != null)
                {
                    currentRange.PartcloneContentRange.EndByte = currentRange.PartcloneContentRange.StartByte + currentRange.OutputFileRange.Length - 1;
                    populatedBlockIndex++;
                }

                if (currentRange.OutputFileRange.EndByte > totalLength)
                {
                    currentRange.OutputFileRange.EndByte = totalLength - 1;
                }
            }

            return result;
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
