using libClonezilla.Cache;
using libClonezilla.Decompressors;
using libClonezilla.PartitionContainers;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libPartclone;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZstdNet;

namespace libClonezilla.Partitions
{
    public class PartclonePartition : Partition
    {
        public PartclonePartition(
            string originFilename, 
            PartitionContainer container, 
            string partitionName, 
            Stream compressedPartcloneStream,
            Compression compressionInUse,
            IPartitionCache? partitionCache,
            bool willPerformRandomSeeking,
            bool processTrailingNulls) : base(container, partitionName, partitionCache, compressedPartcloneStream)
        {
            var streamName = $"[{container.ContainerName}] [{partitionName}]";
            Log.Information($"{streamName} Finding optimal decompressor (seekable/sequential)");

            //No length hint. The decompressed stream here is the partclone IMAGE (header + bitmap + used blocks +
            //checksums), not the partition, so the partition size callers used to pass was the wrong number: for a
            //partition more than ~99.9% full the image is longer, and the restart-based path (lz4, lzip, or a
            //failed index build) then read the last data blocks as end-of-file (gh review F8). Without a hint the
            //restart stream discovers its true length once and persists it; indexed codecs never needed it, and
            //nothing on the sequential path reads it.
            var decompressorSelector = new DecompressorSelector(originFilename, streamName, compressedPartcloneStream, null, compressionInUse, partitionCache, processTrailingNulls);

            Stream decompressedStream;
            if (willPerformRandomSeeking)
            {
                decompressedStream = decompressorSelector.GetSeekableStream();
            }
            else
            {
                decompressedStream = decompressorSelector.GetSequentialStream();
            }

            Log.Information($"{streamName} Loading partition information");
            FullPartitionImage = new PartcloneStream(container.ContainerName, partitionName, decompressedStream);
        }
    }
}
