using clonezilla_util.CL.Verbs;
using CommandLine;
using libClonezilla;
using libClonezilla.Cache;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Sparse;
using libPartclone;
using Serilog;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace clonezilla_util
{
    class Program
    {
        private enum ReturnCode
        {
            Success = 0,
            InvalidArguments = 1,
            GeneralException = 2,
        }

        static int Main(string[] args)
        {
            try
            {
                Log.Logger = new LoggerConfiguration()
                                .WriteTo.Console()
                                .WriteTo.File("clonezilla-util-.log", rollingInterval: RollingInterval.Day)
                                .CreateLogger();

                Log.Debug("Start");

                var types = LoadVerbs();

                Parser.Default.ParseArguments(args, types)
                      .WithParsed(Run)
                      .WithNotParsed(HandleErrors);

                Log.Debug("Exit successfully");
                return (int)ReturnCode.Success;
            }
            catch (Exception ex)
            {
                Log.Error($"Unexpected exception: {ex}");
                return (int)ReturnCode.GeneralException;
            }
        }

        //load all types using Reflection
        private static Type[] LoadVerbs()
        {
            return Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => t.GetCustomAttribute<VerbAttribute>() != null).ToArray();
        }

        private static void Run(object obj)
        {
            ClonezillaCacheManager clonezillaCacheManager = null;

            var startTime = DateTime.Now;

            if (obj is BaseVerb v && v.UseCache)
            {
                string cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
                clonezillaCacheManager = new ClonezillaCacheManager(cacheFolder);
            }

            switch (obj)
            {
                case ExtractPartitionImage extractPartitionImageOptions:
                    var czImage = new ClonezillaImage(extractPartitionImageOptions.InputFolder, clonezillaCacheManager, extractPartitionImageOptions.PartitionsToExtract);

                    var partitionsToExtract = czImage.Partitions;

                    if (!Directory.Exists(extractPartitionImageOptions.OutputFolder))
                    {
                        Directory.CreateDirectory(extractPartitionImageOptions.OutputFolder);
                    }

                    partitionsToExtract
                        .AsParallel().WithDegreeOfParallelism(4)
                        .ForAll(partition =>
                        {
                            var outputFilename = Path.Combine(extractPartitionImageOptions.OutputFolder, $"{partition.Name}.img");

                            Log.Information($"Extracting partition {partition.Name}");
                            Log.Information($"    From: {extractPartitionImageOptions.InputFolder}");
                            Log.Information($"    To: {outputFilename}");
                            Log.Information("");

                            var fileStream = File.Create(outputFilename);
                            ISparseAwareWriter outputStream;

                            if (partition.FullPartitionImage is ISparseAwareReader inputStream)
                            {
                                //a hack to speed things up. Let's make the output file sparse, so that we don't have to write zeroes for all the unpopulated ranges
                                if (libCommon.Utility.IsOnNTFS(outputFilename) && !extractPartitionImageOptions.NoSparseOutput)
                                {
                                    //tell the output stream to create a sparse file
                                    fileStream.SafeFileHandle.MarkAsSparse();
                                    fileStream.SetLength(partition.FullPartitionImage.Length);

                                    //tell the writer not to bother writing  the null bytes to the file (because it's already sparse)
                                    outputStream = new SparseAwareWriteStream(fileStream, false);
                                }
                                else
                                {
                                    inputStream = new SparseAwareReader(partition.FullPartitionImage, true);
                                    outputStream = new SparseAwareWriteStream(fileStream, true);
                                }

                                inputStream
                                    .CopyTo(outputStream, Buffers.SUPER_ARBITARY_LARGE_SIZE_BUFFER,
                                    totalCopied =>
                                    {
                                        var per = (double)totalCopied / partition.FullPartitionImage.Length * 100;

                                        var totalCopiedStr = libCommon.Extensions.BytesToString(totalCopied);
                                        var totalStr = libCommon.Extensions.BytesToString(partition.FullPartitionImage.Length);
                                        Log.Information($"Extracted {totalCopiedStr} / {totalStr} ({per:N0}%)");
                                    });

                                Log.Information("");
                            }
                        });

                    break;

                case ListContents lc:
                    var clonezillaImage = new ClonezillaImage(lc.InputFolder, clonezillaCacheManager, lc.PartitionsToOpen);
                    var partitions = clonezillaImage.Partitions;

                    var allFiles = partitions.SelectMany(partition => partition.GetFilesInPartition().Select(file => new
                    {
                        Partition = partition,
                        File = file
                    }));

                    foreach (var file in allFiles)
                    {
                        string filenameIncludingPartition;
                        if (file.File.FullPath.StartsWith("."))
                        {
                            filenameIncludingPartition = file.File.FullPath.ReplaceFirst(".", file.Partition.Name);
                        }
                        else
                        {
                            filenameIncludingPartition = $"{file.Partition.Name}\\{file.File.FullPath}";
                        }

                        Console.Write(filenameIncludingPartition);
                        if (lc.UseNullSeparator)
                        {
                            Console.Write(char.MinValue);
                        }
                        else
                        {
                            Console.Write(lc.OutputSeparator);
                        }
                    }

                    break;
            }

            var duration = DateTime.Now - startTime;
        }

        private static void HandleErrors(IEnumerable<Error> obj)
        {
            var msg = obj
                        .Select(e => e.ToString())
                        .ToString(Environment.NewLine);
            Log.Error(msg);
        }

        public static void TestSeeking(Stream rawPartitionStream, FileStream outputStream)
        {
            //var chunkSizes = 10 * 1024 * 1024;
            var chunkSizes = 8000;
            var buffer = Buffers.BufferPool.Rent(chunkSizes);

            var ranges = new List<ByteRange>();
            var i = 0L;

            //var totalSize = 415797084160L;
            var totalSize = rawPartitionStream.Length;

            var r = new Random();
            while (i < totalSize)
            {
                var bytesLeft = totalSize - i;
                var len = (long)r.Next(1, chunkSizes + 1);
                len = Math.Min(len, bytesLeft - 1);

                var range = new ByteRange()
                {
                    StartByte = i,
                    EndByte = i + len
                };
                ranges.Add(range);

                i += range.Length;
            }

            outputStream.SafeFileHandle.MarkAsSparse();
            outputStream.SetLength(totalSize);

            ulong totalBytesRead = 0;
            ranges
                .OrderBy(x => Guid.NewGuid())
                .ToList()
                .ForEach(range =>
                {
                    rawPartitionStream.Seek(range.StartByte, SeekOrigin.Begin);
                    var bytesRead = rawPartitionStream.Read(buffer, 0, chunkSizes);

                    outputStream.Seek(range.StartByte, SeekOrigin.Begin);
                    outputStream.Write(buffer, 0, bytesRead);

                    totalBytesRead += (ulong)bytesRead;
                    var percentageComplete = totalBytesRead / (double)outputStream.Length * 100;
                    Log.Information($"{totalBytesRead}    {percentageComplete:N2}%");
                });

            Buffers.BufferPool.Return(buffer);
        }

        public static void TestFullCopy(Stream partcloneStream, Stream outputStream)
        {
            var chunkSizes = 10 * 1024 * 1024;
            var buffer1 = Buffers.BufferPool.Rent(chunkSizes);
            var buffer2 = Buffers.BufferPool.Rent(chunkSizes);

            var lastReport = DateTime.MinValue;
            var totalRead = 0UL;

            using (var compareStream = File.Open(@"E:\3_raw_cz.img", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                while (true)
                {
                    var bytesRead1 = partcloneStream.Read(buffer1, 0, chunkSizes);

                    var bytesRead2 = compareStream.Read(buffer2, 0, chunkSizes);

                    if (bytesRead1 != bytesRead2)
                    {
                        throw new Exception("Different read sizes");
                    }

                    if (!buffer1.IsEqualTo(buffer2))
                    {
                        throw new Exception("Not equal");
                    }



                    if (bytesRead1 == 0)
                    {
                        break;
                    }

                    totalRead += (ulong)bytesRead1;

                    if ((DateTime.Now - lastReport).TotalMilliseconds > 1000)
                    {
                        Log.Information($"{totalRead.BytesToString()}");
                        lastReport = DateTime.Now;
                    }

                    outputStream.Write(buffer1, 0, bytesRead1);
                }
            }

            Buffers.BufferPool.Return(buffer1);
            Buffers.BufferPool.Return(buffer2);
        }
    }
}
