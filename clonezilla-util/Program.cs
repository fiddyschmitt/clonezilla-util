using clonezilla_util.CL.Verbs;
using CommandLine;
using libClonezilla.Cache;
using libClonezilla.PartitionContainers;
using libCommon;
using libPartclone;
using Serilog;
using Serilog.Events;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using static libClonezilla.Partitions.MountedPartitionImage;

namespace clonezilla_util
{
    public class Program
    {
        const string PROGRAM_NAME = "clonezilla-util";
        const string PROGRAM_VERSION = "2.3.1";

        private enum ReturnCode
        {
            Success = 0,
            InvalidArguments = 1,
            GeneralException = 2,
        }

        static readonly string CacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");

        //To get the binary to work when using 'Trim unused code', had to add the TrimMode:
        //  <PublishTrimmed>true</PublishTrimmed>
	    //  <TrimMode>partial</TrimMode>

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ExtractFiles))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ExtractPartitionImage))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(ListContents))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MountAsFiles))]
        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MountAsImageFiles))]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                TempUtility.Cleanup();
                DesktopUtility.Cleanup();
            };

            Log.Logger = new LoggerConfiguration()
                            .MinimumLevel.Debug()
                            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                            .WriteTo.Debug(restrictedToMinimumLevel: LogEventLevel.Debug)
                            .WriteTo.File(@"logs\clonezilla-util-.log", rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: LogEventLevel.Information)
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

            var containers = PartitionContainer.FromPaths(
                listContentsOptions.InputPaths.ToList(), 
                CacheFolder, 
                listContentsOptions.PartitionsToInspect.ToList(), 
                true, 
                vfs, 
                listContentsOptions.ProcessTrailingNulls)
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
            mountAsImageOptions.MountPoint ??= libDokan.Utility.GetAvailableDriveLetter();

            var mountPoint = mountAsImageOptions.MountPoint;
            var vfs = new libClonezilla.VFS.OnDemandVFS(PROGRAM_NAME, mountPoint);

            var containers = PartitionContainer.FromPaths(
                mountAsImageOptions.InputPaths.ToList(), 
                CacheFolder, 
                mountAsImageOptions.PartitionsToMount.ToList(), 
                true, 
                vfs,
                mountAsImageOptions.ProcessTrailingNulls);

            libClonezilla.Utility.PopulateVFS(vfs, vfs.RootFolder.Value, containers, DesiredContent.ImageFiles);

            Log.Information($"Mounting complete. Mounted to: {mountPoint}");
            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        private static void MountAsFiles(MountAsFiles mountAsFilesOptions)
        {
            if (mountAsFilesOptions.InputPaths == null) throw new Exception($"{nameof(mountAsFilesOptions.InputPaths)} not specified.");
            mountAsFilesOptions.MountPoint ??= libDokan.Utility.GetAvailableDriveLetter();

            var mountPoint = mountAsFilesOptions.MountPoint;
            var vfs = new libClonezilla.VFS.OnDemandVFS(PROGRAM_NAME, mountPoint);

            var containers = PartitionContainer.FromPaths(
                mountAsFilesOptions.InputPaths.ToList(), 
                CacheFolder, 
                mountAsFilesOptions.PartitionsToMount.ToList(), 
                true, 
                vfs,
                mountAsFilesOptions.ProcessTrailingNulls);

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
                                vfs,
                                extractPartitionImageOptions.ProcessTrailingNulls);

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

                            //TestFullCopy(partition.FullPartitionImage, Stream.Null, File.OpenRead(@"E:\Temp\2022-08-16-20-img_luks_test_6GB_ext4_zst\ocs_luks_0Yy.ext4.img_from_real_partclone"));

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

        public static void TestFullCopy(Stream partcloneStream, Stream outputStream, Stream compareStream)
        {
            var chunkSizes = 10 * 1024 * 1024;
            var buffer1 = Buffers.BufferPool.Rent(chunkSizes);
            var buffer2 = Buffers.BufferPool.Rent(chunkSizes);

            var lastReport = DateTime.MinValue;
            var totalRead = 0UL;

            //using (var compareStream = File.Open(@"E:\3_raw_cz.img", FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite))
            {
                while (true)
                {
                    Array.Clear(buffer1);
                    Array.Clear(buffer2);

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
