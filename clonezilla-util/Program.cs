using clonezilla_util.CL.Verbs;
using CommandLine;
using lib7Zip;
using lib7Zip.Native;
using libClonezilla.Cache;
using libClonezilla.Extractors;
using libClonezilla.PartitionContainers;
using libClonezilla.VFS;
using libCommon;
using libCommon.Logging;
using libCommon.Streams;
using libPartclone;
using Serilog;
using Serilog.Events;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
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
        const string PROGRAM_VERSION = "2.7.0";

        private enum ReturnCode
        {
            Success = 0,
            InvalidArguments = 1,
            GeneralException = 2,
        }

        // Anchored to the real executable's folder (not AppContext.BaseDirectory): with
        // IncludeAllContentForSelfExtract the bundle self-extracts to a temp dir and BaseDirectory
        // points there, which would otherwise put the cache in %TEMP% instead of beside the exe.
        // (Tool lookups via Utility.Absolutify intentionally still use BaseDirectory, so they
        // resolve to the extracted ext\ folder.)
        static string CacheFolder = Path.Combine(GetExeDirectory(), "cache");

        static string GetExeDirectory()
        {
            // Environment.ProcessPath is the real on-disk executable and stays put even when a
            // single-file bundle self-extracts its content. Fall back to AppContext.BaseDirectory
            // when launched via the dotnet muxer (e.g. `dotnet clonezilla-util.dll`), where
            // ProcessPath is dotnet itself.
            var processPath = Environment.ProcessPath;
            if (processPath != null &&
                !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory;
            }
            return AppContext.BaseDirectory;
        }

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
            };

            Log.Logger = new LoggerConfiguration()
                            .MinimumLevel.Debug()
                            .Filter.With(new SuppressConsecutiveDuplicateFilter())
                            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
                            .WriteTo.Debug(restrictedToMinimumLevel: LogEventLevel.Debug)
                            .WriteTo.File(@"logs\clonezilla-util-.log", rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: LogEventLevel.Information)
                            .CreateLogger();

            Log.Debug("Start");
            PrintProgramVersion();

            var types = LoadVerbs();

            ReturnCode returnCode;
            try
            {
                returnCode = Parser.Default.ParseArguments(args, types)
                                .MapResult(
                                    obj =>
                                    {
                                        Run(obj);
                                        return ReturnCode.Success;
                                    },
                                    HandleErrors);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Unhandled exception");
                returnCode = ReturnCode.GeneralException;
            }

            Log.Debug($"Exiting with code {(int)returnCode} ({returnCode})");
            return (int)returnCode;
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

                if (baseVerb.CacheFolder != null)
                {
                    CacheFolder = baseVerb.CacheFolder;
                }
            }

            WholeFileCacheManager.Initialize(CacheFolder);

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

            var vfs = new Lazy<IVFS>(() =>
            {
                //null mount point = OnDemandVFS picks a free letter at mount time, minimising the
                //choose-vs-mount window in which another process can take the same letter
                var result = new OnDemandVFS(PROGRAM_NAME, null, allowMountPointFallback: true);
                return result;
            });

            var containers = PartitionContainer.FromPaths(
                listContentsOptions.InputPaths.ToList(),
                CacheFolder,
                listContentsOptions.PartitionsToInspect.ToList(),
                true,
                vfs,
                listContentsOptions.ProcessTrailingNulls)
                                .OrderBy(container => container.ContainerName)
                                .ToList();

            var tempFolder = vfs.Value.CreateTempFolder();
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

                            var partitionStream = mountedPartition.Partition.FullPartitionImage
                                ?? throw new Exception($"[{container.ContainerName}] [{partitionName}] {nameof(mountedPartition.Partition.FullPartitionImage)} is not initialised.");
                            var sharedPartitionStream = new SharedStream(partitionStream);

                            // Listing only enumerates - one worker is enough (mounting uses several for
                            // concurrent reads). Dispose it once we've listed; it's not needed after.
                            IExtractor? extractor = null;
                            try
                            {
                                extractor = DetermineExtractor.FindExtractor(
                                    sharedPartitionStream.CreateView,
                                    DetermineExtractor.ListingWorkerCount);

                                if (extractor is IFileListProvider fileListProvider)
                                {
                                    var filesInArchive = mountedPartition.GetFilesInPartition(fileListProvider).ToList();

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
                                }
                                else
                                {
                                    Log.Error($"[{container.ContainerName}] [{partitionName}] Could not find a suitable extractor for this partition. Returning empty file list.");
                                }
                            }
                            catch (NotAnArchiveException)
                            {
                                //expected: this partition has no filesystem 7-Zip can browse (e.g. a raw bios_grub partition).
                                Log.Information($"[{container.ContainerName}] [{partitionName}] No browsable filesystem found in this partition. Listing no files.");
                            }
                            finally
                            {
                                (extractor as IDisposable)?.Dispose();
                            }
                        });
                });
        }

        private static void MountAsImageFiles(MountAsImageFiles mountAsImageOptions)
        {
            if (mountAsImageOptions.InputPaths == null) throw new Exception($"{nameof(mountAsImageOptions.InputPaths)} not specified.");

            //only an auto-chosen drive letter may silently fall back to another; a user-chosen one must not.
            //A null mount point stays null: OnDemandVFS picks a free letter at mount time.
            var mountPointWasAutoSelected = mountAsImageOptions.MountPoint == null;

            var mountPoint = mountAsImageOptions.MountPoint;
            var vfs = new Lazy<IVFS>(() =>
            {
                var result = new OnDemandVFS(PROGRAM_NAME, mountPoint, mountPointWasAutoSelected);
                return result;
            });

            var containers = PartitionContainer.FromPaths(
                mountAsImageOptions.InputPaths.ToList(),
                CacheFolder,
                mountAsImageOptions.PartitionsToMount.ToList(),
                true,
                vfs,
                mountAsImageOptions.ProcessTrailingNulls);

            libClonezilla.Utility.PopulateVFS(vfs, vfs.Value.RootFolder.Value, containers, DesiredContent.ImageFiles);

            //the fallback can land the mount on a different letter than requested
            var mountedAt = vfs.Value.RootFolder.Value.MountPoint;
            Log.Information($"Mounting complete. Mounted to: {mountedAt}");
            Process.Start("explorer.exe", mountedAt);

            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        private static void MountAsFiles(MountAsFiles mountAsFilesOptions)
        {
            if (mountAsFilesOptions.InputPaths == null) throw new Exception($"{nameof(mountAsFilesOptions.InputPaths)} not specified.");

            //only an auto-chosen drive letter may silently fall back to another; a user-chosen one must not.
            //A null mount point stays null: OnDemandVFS picks a free letter at mount time.
            var mountPointWasAutoSelected = mountAsFilesOptions.MountPoint == null;

            var mountPoint = mountAsFilesOptions.MountPoint;
            var vfs = new Lazy<IVFS>(() =>
            {
                var result = new OnDemandVFS(PROGRAM_NAME, mountPoint, mountPointWasAutoSelected);
                return result;
            });

            var containers = PartitionContainer.FromPaths(
                mountAsFilesOptions.InputPaths.ToList(),
                CacheFolder,
                mountAsFilesOptions.PartitionsToMount.ToList(),
                true,
                vfs,
                mountAsFilesOptions.ProcessTrailingNulls);

            libClonezilla.Utility.PopulateVFS(vfs, vfs.Value.RootFolder.Value, containers, DesiredContent.Files);

            //the fallback can land the mount on a different letter than requested
            var mountedAt = vfs.Value.RootFolder.Value.MountPoint;
            Log.Information($"Mounting complete. Mounted to: {mountedAt}");
            Process.Start("explorer.exe", mountedAt);

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


            var vfs = new Lazy<IVFS>(() =>
            {
                //null mount point = OnDemandVFS picks a free letter at mount time, minimising the
                //choose-vs-mount window in which another process can take the same letter
                var result = new OnDemandVFS(PROGRAM_NAME, null, allowMountPointFallback: true);
                return result;
            });

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

        private static ReturnCode HandleErrors(IEnumerable<Error> obj)
        {
            var errors = obj.ToList();

            //help and version requests are not failures
            if (errors.All(e => e is HelpRequestedError or HelpVerbRequestedError or VersionRequestedError))
            {
                return ReturnCode.Success;
            }

            var msg = errors
                        .Select(e => e.ToString() ?? "")
                        .ToString(Environment.NewLine);
            Log.Error(msg);

            return ReturnCode.InvalidArguments;
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
