﻿using lib7Zip;
using libClonezilla.Decompressors;
using libClonezilla.Partitions;
using libCommon;
using libCommon.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.PartitionContainers.ImageFiles
{
    public class CompressedDriveImage : PartitionContainer
    {
        public CompressedDriveImage(string containerName, string filename, List<string> partitionsToLoad, Compression compressionInuse, bool processTrailingNulls)
        {
            ContainerName = containerName;

            var partitionImageFiles = SevenZipUtility.GetArchiveEntries(filename, false, true).ToList();

            var compressedStream = File.OpenRead(filename);
            var decompressorSelector = new DecompressorSelector(filename, ContainerName, compressedStream, null, compressionInuse, null, processTrailingNulls);

            var rawDriveStream = decompressorSelector.GetSeekableStream();

            var container = new RawDriveImage(containerName, partitionsToLoad, rawDriveStream, partitionImageFiles, processTrailingNulls);

            Partitions = container.Partitions;
        }

        public override string ContainerName { get; protected set; }
    }
}
