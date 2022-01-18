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
        public PartclonePartition(PartitionContainer container, string partitionName, Stream compressedPartcloneStream, long? uncompressedLength, Compression compressionInUse, IPartitionCache? partitionCache, IPartcloneCache? partcloneCache, bool willPerformRandomSeeking)
            : base(container, partitionName, partitionCache)
        {
            Log.Information($"[{container.Name}] [{partitionName}] Finding optimal decompressor");

            var decompressorSelector = new DecompressorSelector(container.Name, partitionName, compressedPartcloneStream, uncompressedLength, compressionInUse, partitionCache);

            Stream decompressedStream;
            if (willPerformRandomSeeking)
            {
                decompressedStream = decompressorSelector.GetSeekableStream();
            }
            else
            {
                decompressedStream = decompressorSelector.GetSequentialStream();
            }

            Log.Information($"[{container.Name}] [{partitionName}] Loading partition information");
            FullPartitionImage = new PartcloneStream(container.Name, partitionName, decompressedStream, partcloneCache);
        }
    }
}
