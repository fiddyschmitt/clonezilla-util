﻿using libClonezilla.Cache;
using libClonezilla.Decompressors;
using libClonezilla.PartitionContainers;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Partitions
{
    public class ImageFilePartition : Partition
    {
        public ImageFilePartition(
            string originFilename, 
            PartitionContainer container, 
            string partitionName, 
            Stream stream, 
            long? uncompressedSize, 
            Compression compressionInUse, 
            IPartitionCache? 
            partitionCache, 
            bool willPerformRandomSeeking,
            bool processTrailingNulls) : base(container, partitionName, partitionCache, stream)
        {
            var streamName = $"[{container.ContainerName}] [{partitionName}]";

            Log.Information($"{streamName} Finding optimal decompressor (seekable/sequential)");
            
            var decompressorSelector = new DecompressorSelector(originFilename, streamName, stream, uncompressedSize, compressionInUse, partitionCache, processTrailingNulls);

            Stream decompressedStream;
            if (willPerformRandomSeeking)
            {
                decompressedStream = decompressorSelector.GetSeekableStream();
            }
            else
            {
                decompressedStream = decompressorSelector.GetSequentialStream();
            }

            Log.Information($"[{container.ContainerName}] [{partitionName}] Loading partition information");
            FullPartitionImage = decompressedStream;
        }
    }
}
