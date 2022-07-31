using clonezilla_util.CL.Verbs;
using clonezilla_util.VFS;
using CommandLine;
using DokanNet;
using lib7Zip;
using libClonezilla;
using libClonezilla.Cache;
using libClonezilla.PartitionContainers;
using libClonezilla.Partitions;
using libClonezilla.VFS;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Seekable;
using libCommon.Streams.Sparse;
using libDokan;
using libDokan.VFS;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using libPartclone;
using Serilog;
using Serilog.Core;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using static libClonezilla.Partitions.MountedPartitionImage;

namespace clonezilla_util
{
    public class Program
    {
        const string PROGRAM_NAME = "clonezilla-util";
        const string PROGRAM_VERSION = "1.6.0";

        private enum ReturnCode
        {
            Success = 0,
            InvalidArguments = 1,
            GeneralException = 2,
        }

        public static string CacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");

        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                TempUtility.Cleanup();
                DesktopUtility.Cleanup();
            };

            Log.Logger = new LoggerConfiguration()
                            //.MinimumLevel.Debug()
                            .WriteTo.Console()
                            .WriteTo.File(@"logs\clonezilla-util-.log", rollingInterval: RollingInterval.Day)
                            .CreateLogger();

            WholeFileCacheManager.Initialize(CacheFolder);

            Log.Debug("Start");
            PrintProgramVersion();

            var types = LoadVerbs();

            Parser.Default.ParseArguments(args, types)
                  .WithParsed(Run)
                  .WithNotParsed(HandleErrors);

            Log.Debug("Exit successfully");
            return (int)ReturnCode.Success;
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
            if (obj is BaseVerb baseVerb)
            {
                if (baseVerb.TempFolder != null)
                {
                    TempUtility.TempRoot = baseVerb.TempFolder;
                }
            }

            switch (obj)
            {
                case ListContents listContentsOptions:

                    ListContents(listContentsOptions);
                    break;

                case MountAsImageFiles mountAsImageOptions:

                    MountAsImageFiles(mountAsImageOptions);
                    break;

                case MountAsFiles mountAtFilesOptions:

                    MountAsFiles(mountAtFilesOptions);
                    break;

                case ExtractPartitionImage extractPartitionImageOptions:

                    ExtractPartitionImage(extractPartitionImageOptions);
                    break;
            }
        }

        private static void ListContents(ListContents listContentsOptions)
        {
            if (listContentsOptions.InputPaths == null) throw new Exception($"{nameof(listContentsOptions.InputPaths)} not specified.");

            var mountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var vfs = new libClonezilla.VFS.OnDemandVFS(PROGRAM_NAME, mountPoint);

            var containers = PartitionContainer.FromPaths(listContentsOptions.InputPaths.ToList(), CacheFolder, listContentsOptions.PartitionsToInspect.ToList(), true, vfs)
                                .OrderBy(container => container.ContainerName)
                                .ToList();

            var tempFolder = vfs.CreateTempFolder();
            var mountedContainers = libClonezilla.Utility.PopulateVFS(vfs, tempFolder, containers, DesiredContent.ImageFiles);

            var partitions = containers
                                .SelectMany(container => container.Partitions)
                                .ToList();

            mountedContainers
                .ForEach(mountedContainer =>
                {
                    var mountedPartitions = mountedContainer.MountedPartitions;

                    mountedPartitions
                        .ForEach(mountedPartition =>
                        {
                            var container = mountedPartition.Partition.Container;
                            var partitionName = mountedPartition.Partition.PartitionName;

                            Log.Information($"[{container.ContainerName}] [{partitionName}] Retrieving a list of files.");

                            //todo: Consider using GetFiles() instead
                            var filesInArchive = mountedPartition.GetFilesInPartition();

                            foreach (var archiveEntry in filesInArchive)
                            {
                                var filenameIncludingPartition = Path.Combine(container.ContainerName, partitionName, archiveEntry.Path);

                                Console.Write(filenameIncludingPartition);
                                if (listContentsOptions.UseNullSeparator)
                                {
                                    Console.Write(char.MinValue);
                                }
                                else
                                {
                                    Console.Write(listContentsOptions.OutputSeparator);
                                }
                            }
                        });
                });
        }

        private static void MountAsImageFiles(MountAsImageFiles mountAsImageOptions)
        {
            if (mountAsImageOptions.InputPaths == null) throw new Exception($"{nameof(mountAsImageOptions.InputPaths)} not specified.");
            if (mountAsImageOptions.MountPoint == null) mountAsImageOptions.MountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var mountPoint = mountAsImageOptions.MountPoint;
            var vfs = new libClonezilla.VFS.OnDemandVFS(PROGRAM_NAME, mountPoint);

            var containers = PartitionContainer.FromPaths(mountAsImageOptions.InputPaths.ToList(), CacheFolder, mountAsImageOptions.PartitionsToMount.ToList(), true, vfs);

            libClonezilla.Utility.PopulateVFS(vfs, vfs.RootFolder.Value, containers, DesiredContent.ImageFiles);

            Log.Information($"Mounting complete. Mounted to: {mountPoint}");
            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        private static void MountAsFiles(MountAsFiles mountAsFilesOptions)
        {
            if (mountAsFilesOptions.InputPaths == null) throw new Exception($"{nameof(mountAsFilesOptions.InputPaths)} not specified.");
            if (mountAsFilesOptions.MountPoint == null) mountAsFilesOptions.MountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var mountPoint = mountAsFilesOptions.MountPoint;
            var vfs = new libClonezilla.VFS.OnDemandVFS(PROGRAM_NAME, mountPoint);

            var containers = PartitionContainer.FromPaths(mountAsFilesOptions.InputPaths.ToList(), CacheFolder, mountAsFilesOptions.PartitionsToMount.ToList(), true, vfs);

            libClonezilla.Utility.PopulateVFS(vfs, vfs.RootFolder.Value, containers, DesiredContent.Files);

            Log.Information($"Mounting complete. Mounted to: {mountPoint}");
            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        private static void ExtractPartitionImage(ExtractPartitionImage extractPartitionImageOptions)
        {
            if (extractPartitionImageOptions.InputPaths == null) throw new Exception($"{nameof(extractPartitionImageOptions.InputPaths)} not specified.");
            if (extractPartitionImageOptions.OutputFolder == null) throw new Exception($"{nameof(extractPartitionImageOptions.OutputFolder)} not specified.");

            if (!Directory.Exists(extractPartitionImageOptions.OutputFolder))
            {
                Directory.CreateDirectory(extractPartitionImageOptions.OutputFolder);
            }

            var mountPoint = libDokan.Utility.GetAvailableDriveLetter();
            var vfs = new libClonezilla.VFS.OnDemandVFS(PROGRAM_NAME, mountPoint);

            var containers = PartitionContainer.FromPaths(
                                extractPartitionImageOptions.InputPaths.ToList(),
                                CacheFolder,
                                extractPartitionImageOptions.PartitionsToExtract.ToList(),
                                false,
                                vfs);

            containers
                .ForEach(container =>
                {
                    var partitionsToExtract = container.Partitions;

                    partitionsToExtract
                        .ForEach(partition =>
                        {
                            string outputFilename;
                            if (containers.Count == 1)
                            {
                                outputFilename = Path.Combine(extractPartitionImageOptions.OutputFolder, $"{partition.PartitionName}.img");
                            }
                            else
                            {
                                outputFilename = Path.Combine(extractPartitionImageOptions.OutputFolder, $"{container.ContainerName}.{partition.PartitionName}.img");
                            }

                            var makeSparse = !extractPartitionImageOptions.NoSparseOutput;
                            partition.ExtractToFile(outputFilename, makeSparse);
                        });
                });
        }

        private static void HandleErrors(IEnumerable<Error> obj)
        {
            var msg = obj
                        .Select(e => e.ToString() ?? "")
                        .ToString(Environment.NewLine);
            Log.Error(msg);
        }

        [SupportedOSPlatform("windows")]
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
