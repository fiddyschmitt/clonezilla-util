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
using Serilog.Core;
using Serilog.Events;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static libClonezilla.Partitions.MountedPartitionImage;

namespace clonezilla_util
{
    public class Program
    {
        const string PROGRAM_NAME = "clonezilla-util";

        //Version has ONE source of truth: <Version> in clonezilla-util.csproj. Read it back from the
        //assembly here so the startup banner and CommandLineParser's --help header can never disagree.
        static readonly string PROGRAM_VERSION =
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        // Runtime-adjustable log level. Default Information: the mount hot paths then never format or
        // emit the per-operation Debug traces, whose WriteTo.Debug (OutputDebugString) sink and its
        // global lock cost ~22% of thread-time under load (TEST_ANALYSIS.md Lead L12). --verbose flips
        // this to Debug once the verb is parsed, restoring the old firehose for troubleshooting.
        static readonly LoggingLevelSwitch LogLevelSwitch = new(LogEventLevel.Information);

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
                            .MinimumLevel.ControlledBy(LogLevelSwitch)
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
                if (baseVerb.Verbose)
                {
                    LogLevelSwitch.MinimumLevel = LogEventLevel.Debug;
                    Log.Debug("Verbose logging enabled.");
                }

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

                case ExtractFiles extractFilesOptions:

                    ExtractFiles(extractFilesOptions);
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

                            // With a warm cache, listing needs no extractor at all: opening the native
                            // 7z workers makes 7z scan the filesystem through the compressed stream,
                            // which cost warm xz listings ~100 s for nothing (TEST_ANALYSIS.md #3).
                            // Only a cache miss constructs one (to enumerate; disposed straight after).
                            IExtractor? extractor = null;
                            try
                            {
                                List<ArchiveEntry> filesInArchive;
                                var cachedList = mountedPartition.Partition.PartitionCache?.GetFileList();
                                if (cachedList != null)
                                {
                                    filesInArchive = cachedList
                                        .Where(entry => !Path.GetFileName(entry.Path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                                        .ToList();
                                }
                                else
                                {
                                    var partitionStream = mountedPartition.Partition.FullPartitionImage
                                        ?? throw new Exception($"[{container.ContainerName}] [{partitionName}] {nameof(mountedPartition.Partition.FullPartitionImage)} is not initialised.");
                                    var sharedPartitionStream = new SharedStream(partitionStream);

                                    // Listing only enumerates - one worker is enough (mounting uses several for
                                    // concurrent reads).
                                    extractor = DetermineExtractor.FindExtractor(
                                        sharedPartitionStream.CreateView,
                                        DetermineExtractor.ListingWorkerCount);

                                    if (extractor is IFileListProvider fileListProvider)
                                    {
                                        filesInArchive = mountedPartition.GetFilesInPartition(fileListProvider).ToList();
                                    }
                                    else
                                    {
                                        Log.Error($"[{container.ContainerName}] [{partitionName}] Could not find a suitable extractor for this partition. Returning empty file list.");
                                        filesInArchive = [];
                                    }
                                }

                                {
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
            if (!mountAsImageOptions.NoExplorer) Process.Start("explorer.exe", mountedAt);

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
            if (!mountAsFilesOptions.NoExplorer) Process.Start("explorer.exe", mountedAt);

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

        private static void ExtractFiles(ExtractFiles extractOptions)
        {
            if (extractOptions.InputPaths == null) throw new Exception($"{nameof(extractOptions.InputPaths)} not specified.");

            var inputPaths = extractOptions.InputPaths.ToList();
            var outputRoot = ResolveExtractOutputRoot(extractOptions.OutputFolder, inputPaths);
            Directory.CreateDirectory(outputRoot);

            var filter = new PathGlobFilter(extractOptions.Include, extractOptions.Exclude);

            var vfs = new Lazy<IVFS>(() =>
            {
                //null mount point = OnDemandVFS picks a free letter at mount time. Extract never actually
                //mounts (this Lazy stays unforced) - it enumerates and copies streams directly, headless.
                var result = new OnDemandVFS(PROGRAM_NAME, null, allowMountPointFallback: true);
                return result;
            });

            var containers = PartitionContainer.FromPaths(
                                inputPaths,
                                CacheFolder,
                                extractOptions.PartitionsToExtract.ToList(),
                                true,   //random access: we seek to scattered individual files
                                vfs,
                                extractOptions.ProcessTrailingNulls);

            long totalFiles = 0;
            long totalBytes = 0;
            long failedFiles = 0;

            containers.ForEach(container =>
            {
                container.Partitions.ForEach(partition =>
                {
                    var containerName = container.ContainerName;
                    var partitionName = partition.PartitionName;

                    //preserve-mode output nests under a partition-named folder; prefix with the container
                    //name too when several containers could otherwise collide on the same partition name.
                    var prefix = containers.Count == 1 ? partitionName : $"{containerName}.{partitionName}";

                    IExtractor? extractor = null;
                    try
                    {
                        var partitionStream = partition.FullPartitionImage
                            ?? throw new Exception($"[{containerName}] [{partitionName}] {nameof(partition.FullPartitionImage)} is not initialised.");
                        var sharedPartitionStream = new SharedStream(partitionStream);

                        //MountWorkerCount (not ListingWorkerCount): the same pool serves the file list AND
                        //the parallel content reads below.
                        extractor = DetermineExtractor.FindExtractor(
                            sharedPartitionStream.CreateView,
                            DetermineExtractor.MountWorkerCount);

                        if (extractor is not IFileListProvider fileListProvider)
                        {
                            Log.Error($"[{containerName}] [{partitionName}] Could not find a suitable extractor for this partition. Skipping.");
                            return;
                        }

                        var matches = fileListProvider.GetFileList()
                            .Where(e => !e.IsFolder)
                            .Where(e => !Path.GetFileName(e.Path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                            .Where(e => IsExtractableToFile(e.Path))
                            .Where(e => filter.Matches(Path.Combine(containerName, partitionName, e.Path)))
                            .ToList();

                        if (matches.Count == 0)
                        {
                            Log.Information($"[{containerName}] [{partitionName}] No files matched.");
                            return;
                        }

                        Log.Information($"[{containerName}] [{partitionName}] Extracting {matches.Count} file(s) to: {outputRoot}");

                        var seenFlatNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var localExtractor = extractor;

                        //Each iteration owns its own PooledNativeItemStream and reads it on one thread; the
                        //worker pool serialises native access. This is the concurrency model the mount path
                        //exercises under the ConcurrentBleedStress gate.
                        Parallel.ForEach(
                            matches,
                            new ParallelOptions { MaxDegreeOfParallelism = DetermineExtractor.MountWorkerCount },
                            entry =>
                            {
                                string destination;
                                if (extractOptions.Flatten)
                                {
                                    var name = Path.GetFileName(entry.Path);
                                    lock (seenFlatNames)
                                    {
                                        if (!seenFlatNames.Add(name))
                                            Log.Warning($"[{containerName}] [{partitionName}] --flatten name collision, overwriting: {name}");
                                    }
                                    destination = Path.Combine(outputRoot, name);
                                }
                                else
                                {
                                    destination = Path.Combine(outputRoot, prefix, entry.Path);
                                }

                                try
                                {
                                    var destinationDir = Path.GetDirectoryName(destination);
                                    if (destinationDir != null) Directory.CreateDirectory(destinationDir);

                                    using (var source = localExtractor.Extract(entry.Path))
                                    using (var fileStream = File.Create(destination))
                                    {
                                        source.CopyTo(fileStream, Buffers.ARBITRARY_LARGE_SIZE_BUFFER);
                                    }

                                    TrySetTimestamps(destination, entry);

                                    Interlocked.Increment(ref totalFiles);
                                    Interlocked.Add(ref totalBytes, entry.Size);
                                }
                                catch (Exception ex)
                                {
                                    //one unwritable name (odd characters, path too long, ...) must not abort
                                    //the whole extract - report it and carry on.
                                    Interlocked.Increment(ref failedFiles);
                                    Log.Warning($"[{containerName}] [{partitionName}] Could not extract '{entry.Path}': {ex.Message}");
                                }
                            });
                    }
                    catch (NotAnArchiveException)
                    {
                        //expected: this partition has no filesystem 7-Zip can browse (e.g. a raw bios_grub partition).
                        Log.Information($"[{containerName}] [{partitionName}] No browsable filesystem found in this partition. Nothing to extract.");
                    }
                    finally
                    {
                        (extractor as IDisposable)?.Dispose();
                    }
                });
            });

            Log.Information($"Extracted {totalFiles} file(s), {totalBytes.BytesToString()}, to: {outputRoot}");
            if (failedFiles > 0) Log.Warning($"{failedFiles} file(s) could not be extracted (see warnings above).");
        }

        //Skip archive entries that can't be written as a distinct file on the host: NTFS alternate data
        //streams (7z spells them "name:stream", and File.Create would silently write into the base file's
        //stream), and the '.'/'..' pseudo-entries the NTFS handler emits. Real files - including NTFS
        //$-metafiles - are kept.
        private static bool IsExtractableToFile(string archivePath)
        {
            var leaf = Path.GetFileName(archivePath);
            if (leaf is "." or "..") return false;
            if (archivePath.Contains(':')) return false;
            return true;
        }

        //Resolve the extract output folder. An explicit -o wins; otherwise default to a new subfolder in the
        //current directory named after the input (the folder's name, or a file's name without its extension),
        //never writing into the input itself.
        private static string ResolveExtractOutputRoot(string? outputOption, List<string> inputPaths)
        {
            if (!string.IsNullOrWhiteSpace(outputOption)) return outputOption;

            var firstInput = inputPaths.First().TrimEnd('\\', '/');
            var baseName = Directory.Exists(firstInput)
                ? new DirectoryInfo(firstInput).Name
                : Path.GetFileNameWithoutExtension(firstInput);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "extracted";

            var candidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), baseName));

            var inputFull = Path.GetFullPath(firstInput);
            if (candidate.Equals(inputFull, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(inputFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), baseName + "-extracted"));
            }

            return candidate;
        }

        private static void TrySetTimestamps(string path, ArchiveEntry entry)
        {
            //best-effort: some entries carry no/degenerate timestamps, and pre-1601 dates are invalid for
            //the Win32 filetime APIs - the try/catch keeps extraction going regardless.
            try
            {
                if (entry.Modified > DateTime.MinValue) File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(entry.Modified, DateTimeKind.Utc));
                if (entry.Created > DateTime.MinValue) File.SetCreationTimeUtc(path, DateTime.SpecifyKind(entry.Created, DateTimeKind.Utc));
            }
            catch { }
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
