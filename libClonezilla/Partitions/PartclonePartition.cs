using libClonezilla.Cache;
using libClonezilla.Decompressors;
using libClonezilla.PartitionContainers;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libGZip;
using libPartclone;
using libPartclone.Cache;
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
        public PartclonePartition(string originFilename, PartitionContainer container, string partitionName, Stream compressedPartcloneStream, long? uncompressedLength, Compression compressionInUse, IPartitionCache? partitionCache, IPartcloneCache? partcloneCache, bool willPerformRandomSeeking) : base(container, partitionName, partitionCache, compressedPartcloneStream)
        {
            var streamName = $"[{container.ContainerName}] [{partitionName}]";
            Log.Information($"{streamName} Finding optimal decompressor (seekable/sequential)");

            var decompressorSelector = new DecompressorSelector(originFilename, streamName, compressedPartcloneStream, uncompressedLength, compressionInUse, partitionCache);

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
            FullPartitionImage = new PartcloneStream(container.ContainerName, partitionName, decompressedStream, partcloneCache);
        }
    }
}
