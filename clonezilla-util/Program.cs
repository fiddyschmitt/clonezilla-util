using clonezilla_util.CL.Verbs;
using CommandLine;
using DokanNet;
using lib7Zip;
using libClonezilla;
using libClonezilla.Cache;
using libClonezilla.PartitionContainers;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Sparse;
using libDokan;
using libPartclone;
using Serilog;
using Serilog.Core;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace clonezilla_util
{
    class Program
    {
        const string PROGRAM_NAME = "clonezilla-util";
        const string PROGRAM_VERSION = "1.2";

        private enum ReturnCode
        {
            Success = 0,
            InvalidArguments = 1,
            GeneralException = 2,
        }

        static int Main(string[] args)
        {
            //try
            {
                Log.Logger = new LoggerConfiguration()
                                //.MinimumLevel.Debug()
                                .WriteTo.Console()
                                .WriteTo.File(@"logs\clonezilla-util-.log", rollingInterval: RollingInterval.Day)
                                .CreateLogger();

                Log.Debug("Start");
                PrintProgramVersion();

                var types = LoadVerbs();

                Parser.Default.ParseArguments(args, types)
                      .WithParsed(Run)
                      .WithNotParsed(HandleErrors);

                Log.Debug("Exit successfully");
                return (int)ReturnCode.Success;
            }
            /*
            catch (Exception ex)
            {
                Log.Error($"Unexpected exception: {ex}");
                return (int)ReturnCode.GeneralException;
            }
            */
        }

        static void PrintProgramVersion()
        {
            string fullProgramName = $"{PROGRAM_NAME} v{PROGRAM_VERSION}";
            Log.Information(fullProgramName);
        }

        //load all types using Reflection
        private static Type[] LoadVerbs()
        {
            return Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => t.GetCustomAttribute<VerbAttribute>() != null).ToArray();
        }

        private static void Run(object obj)
        {
            ClonezillaCacheManager? clonezillaCacheManager = null;

            var startTime = DateTime.Now;

            string cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            clonezillaCacheManager = new ClonezillaCacheManager(cacheFolder);

            switch (obj)
            {
                case ExtractPartitionImage extractPartitionImageOptions:

                    if (extractPartitionImageOptions.InputPath == null) throw new Exception($"{nameof(extractPartitionImageOptions.InputPath)} not specified.");
                    if (extractPartitionImageOptions.OutputFolder == null) throw new Exception($"{nameof(extractPartitionImageOptions.OutputFolder)} not specified.");

                    var partitionContainerType = IPartitionContainer.FromPath(extractPartitionImageOptions.InputPath);

                    IPartitionContainer partitionContainer = partitionContainerType switch
                    {
                        PartitionContainerType.ClonezillaFolder => new ClonezillaImage(extractPartitionImageOptions.InputPath, clonezillaCacheManager, extractPartitionImageOptions.PartitionsToExtract, false),
                        PartitionContainerType.PartcloneFile => new PartcloneFile(extractPartitionImageOptions.InputPath, false),
                        _ => throw new NotImplementedException()
                    };

                    var partitionsToExtract = partitionContainer.Partitions;

                    if (!Directory.Exists(extractPartitionImageOptions.OutputFolder))
                    {
                        Directory.CreateDirectory(extractPartitionImageOptions.OutputFolder);
                    }

                    partitionsToExtract
                        .ForEach(partition =>
                        {
                            var outputFilename = Path.Combine(extractPartitionImageOptions.OutputFolder, $"{partition.Name}.img");

                            var makeSparse = !extractPartitionImageOptions.NoSparseOutput;
                            partition.ExtractToFile(outputFilename, makeSparse);
                        });

                    break;

                case MountAsImageFiles mountAsImageOptions:

                    if (mountAsImageOptions.InputPath == null) throw new Exception($"{nameof(mountAsImageOptions.InputPath)} not specified.");
                    if (mountAsImageOptions.MountPoint == null) throw new Exception($"{nameof(mountAsImageOptions.MountPoint)} not specified.");

                    partitionContainerType = IPartitionContainer.FromPath(mountAsImageOptions.InputPath);

                    partitionContainer = partitionContainerType switch
                    {
                        PartitionContainerType.ClonezillaFolder => new ClonezillaImage(mountAsImageOptions.InputPath, clonezillaCacheManager, mountAsImageOptions.PartitionsToExtract, true),
                        PartitionContainerType.PartcloneFile => new PartcloneFile(mountAsImageOptions.InputPath, true),
                        _ => throw new NotImplementedException()
                    };

                    var virtualFileEntries = partitionContainer
                                                .Partitions
                                                .Select(partition => new FileEntry($"{partition.Name}.img", () => partition.FullPartitionImage)
                                                {
                                                    Created = DateTime.Now,
                                                    Modified = DateTime.Now,
                                                    Accessed = DateTime.Now,
                                                    Length = partition.FullPartitionImage.Length
                                                })
                                                .ToList();

                    var root = new Folder("");
                    root.Children.AddRange(virtualFileEntries);

                    Log.Information($"Mounting partition images to: {mountAsImageOptions.MountPoint}");

                    var vfs = new DokanVFS(root, PROGRAM_NAME);

                    vfs.Mount(mountAsImageOptions.MountPoint, DokanOptions.WriteProtection, new DokanNet.Logging.NullLogger());

                    break;

                case ListContents lc:
                    if (lc.InputFolder == null) throw new Exception($"InputFolder not specified.");

                    var clonezillaImage = new ClonezillaImage(lc.InputFolder, clonezillaCacheManager, lc.PartitionsToOpen, true);
                    var partitions = clonezillaImage.Partitions;

                    var allFiles = partitions.SelectMany(partition => partition.GetFilesInPartition().Select(file => new
                    {
                        Partition = partition,
                        File = file
                    }));

                    foreach (var file in allFiles)
                    {
                        if (file.File.FullPath == null) continue;

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
                        .Select(e => e.ToString() ?? "")
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

            using (var compareStream = File.Open(@"E:\3_raw_cz.img", FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite))
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
