using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;
using System.Linq;
using System.IO;
using libCommon.Streams;
using libGZip;
using libCommon;
using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
using Serilog;
using libCommon.Streams.Sparse;
using ZstdNet;
using libCommon.Streams.Seekable;
using lib7Zip;
using libDokan.VFS.Folders;
using libDokan.VFS.Files;
using libClonezilla.PartitionContainers;

namespace libClonezilla.Partitions
{
    public abstract class Partition
    {
        public Stream? FullPartitionImage { get; protected set; }
        public PartitionContainer Container { get; protected set; }
        public string PartitionName { get; protected set; }
        public IPartitionCache? PartitionCache { get; protected set; }

        public Partition(PartitionContainer container, string partitionName, IPartitionCache? partitionCache)
        {
            Container = container;
            PartitionName = partitionName;
            PartitionCache = partitionCache;
        }

        public void ExtractToFile(string outputFilename, bool makeSparse)
        {
            if (FullPartitionImage == null) throw new Exception($"[{Container.ContainerName}] [{PartitionName}] Cannot extract. {nameof(FullPartitionImage)} has not been intialised.");

            ExtractToFile(Container.ContainerName, PartitionName, FullPartitionImage, outputFilename, makeSparse);
        }

        public static void ExtractToFile(string containerName, string partitionName, Stream stream, string outputFilename, bool makeSparse)
        {
            Log.Information($"[{containerName}] [{partitionName}] Extracting partition to: {outputFilename}");

            using var fileStream = File.Create(outputFilename);
            ExtractToFile(containerName, partitionName, stream, fileStream, makeSparse);
        }

        public static void ExtractToFile(string containerName, string partitionName, Stream inputStream, FileStream fileStream, bool makeSparse)
        {
            if (libCommon.Utility.IsOnNTFS(fileStream.Name) && makeSparse && inputStream is ISparseAwareReader sparseAwareInput)
            {
                //a hack to speed things up. Let's make the output file sparse, so that we don't have to write zeroes for all the unpopulated ranges

                //tell the input stream to not bother with the remainder of the file if it's all null
                sparseAwareInput.StopReadingWhenRemainderOfFileIsNull = true;

                //tell the output stream to create a sparse file
                if (OperatingSystem.IsWindows())
                {
                    fileStream.SafeFileHandle.MarkAsSparse();
                }
                fileStream.SetLength(inputStream.Length);

                //tell the writer not to bother writing the null bytes to the file (because it's already sparse)
                var outputStream = new SparseAwareWriteStream(fileStream, false);

                sparseAwareInput
                    .Sparsify(outputStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                    totalCopied =>
                    {
                        var per = (double)totalCopied / inputStream.Length * 100;

                        var totalCopiedStr = Extensions.BytesToString(totalCopied);
                        var totalStr = Extensions.BytesToString(inputStream.Length);
                        Log.Information($"Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                    });
            }
            else
            {
                //just a regular file, with null bytes and all
                inputStream
                    .CopyTo(fileStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                    totalCopied =>
                    {
                        var per = (double)totalCopied / inputStream.Length * 100;

                        var totalCopiedStr = Extensions.BytesToString(totalCopied);
                        var totalStr = Extensions.BytesToString(inputStream.Length);
                        Log.Information($"[{containerName}] [{partitionName}] Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                    });
            }
        }
    }
}
