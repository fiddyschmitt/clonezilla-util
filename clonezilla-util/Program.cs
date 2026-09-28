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
using libCommon.Memory;
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
            PartialFailure = 3,   //the verb ran, but some items could not be processed (e.g. extract skipped files)
            DokanNotInstalled = 4,
            MountFailed = 5,      //e.g. the mount point is taken by another volume
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
            //stdout in UTF-8 so `list` prints non-ASCII file names correctly when redirected (the Windows
            //default is the OEM code page, which renders them as '?') (gh review F47). BOM-less, so a
            //redirected `list > files.txt` has no 3-byte prefix on its first record. Guarded: the setter
            //can throw when there is no real console attached.
            try { System.Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false); } catch { }

            Log.Logger = new LoggerConfiguration()
                            .MinimumLevel.ControlledBy(LogLevelSwitch)
                            .Filter.With(new SuppressConsecutiveDuplicateFilter())
                            //All log output goes to stderr (standardErrorFromLevel at the lowest level) so
                            //that `list` can write clean, machine-readable records to stdout - a redirected
                            //`list > files.txt` must hold only the file identities, never the banner or the
                            //progress lines (gh review F47).
                            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information, standardErrorFromLevel: LogEventLevel.Verbose)
                            .WriteTo.Debug(restrictedToMinimumLevel: LogEventLevel.Debug)
                            .WriteTo.File(@"logs\clonezilla-util-.log", rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: LogEventLevel.Information)
                            .CreateLogger();

            //A crash on a worker thread still ends the process, but now it is logged first (gh review F56).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception; the process is terminating.");
                Log.CloseAndFlush();
            };

            Log.Debug("Start");
            PrintProgramVersion();

            var types = LoadVerbs();

            ReturnCode returnCode;
            try
            {
                returnCode = Parser.Default.ParseArguments(args, types)
                                .MapResult(
                                    obj => Run(obj),
                                    HandleErrors);
            }
            catch (libClonezilla.FatalException ex)
            {
                //an expected way for a run to end (thrown by library code instead of Environment.Exit - gh review F56)
                Log.Fatal(ex.Message);
                returnCode = ex.Reason switch
                {
                    libClonezilla.FatalReason.DokanNotInstalled => ReturnCode.DokanNotInstalled,
                    libClonezilla.FatalReason.MountFailed => ReturnCode.MountFailed,
                    _ => ReturnCode.InvalidArguments,
                };
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

        private static ReturnCode Run(object obj)
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
                    Log.Warning("--temp-folder is ignored: clonezilla-util keeps decoded data in memory and writes no temporary files.");
                }

                if (baseVerb.CacheFolder != null)
                {
                    CacheFolder = baseVerb.CacheFolder;
                }
            }

            //Memory governor: the decoded-span cache grows to fill RAM down to a reserve (not a fixed
            //fraction of total RAM). Resolve reserve/cap CLI > env > default, before any cache is built.
            var verb = obj as BaseVerb;
            long? reserveMb = verb?.MemoryReserveMb;
            string reserveSource = reserveMb != null ? "--memory-reserve" : "default (8% RAM)";
            if (reserveMb == null && long.TryParse(Environment.GetEnvironmentVariable("CLONEZILLA_MEMORY_RESERVE_MB"), out var envReserveMb) && envReserveMb > 0)
            {
                reserveMb = envReserveMb;
                reserveSource = "CLONEZILLA_MEMORY_RESERVE_MB";
            }
            long? cacheMaxMb = verb?.CacheMaxMb;
            if (cacheMaxMb == null && long.TryParse(Environment.GetEnvironmentVariable("CLONEZILLA_CACHE_MAX_MB"), out var envCacheMaxMb) && envCacheMaxMb > 0)
            {
                cacheMaxMb = envCacheMaxMb;
            }
            MemoryGovernor.Configure(reserveMb, cacheMaxMb, reserveSource);

            WholeFileCacheManager.Initialize(CacheFolder);

            switch (obj)
            {
                case ListContents listContentsOptions:

                    ListContents(listContentsOptions);
                    return ReturnCode.Success;

                case MountAsImageFiles mountAsImageOptions:

                    return MountAsImageFiles(mountAsImageOptions);

                case MountAsFiles mountAtFilesOptions:

                    return MountAsFiles(mountAtFilesOptions);

                case ExtractPartitionImage extractPartitionImageOptions:

                    ExtractPartitionImage(extractPartitionImageOptions);
                    return ReturnCode.Success;

                case ExtractFiles extractFilesOptions:

                    return ExtractFiles(extractFilesOptions);
            }

            return ReturnCode.Success;
        }

        private static void ListContents(ListContents listContentsOptions)
        {
            if (listContentsOptions.InputPaths == null) throw new Exception($"{nameof(listContentsOptions.InputPaths)} not specified.");

            //The listing reads each partition's image stream in-process and never mounts anything, for any input:
            //no drive letter, no sentinel probe or mount wait, and no Dokan needed. (It used to mount a temp
            //folder it never read - gh review F45 - and compressed image files were decompressed through a temp
            //mount - gh review F46.)
            var containers = PartitionContainer.FromPaths(
                listContentsOptions.InputPaths.ToList(),
                CacheFolder,
                listContentsOptions.PartitionsToInspect.ToList(),
                true,
                listContentsOptions.ProcessTrailingNulls)
                                .OrderBy(container => container.ContainerName)
                                .ToList();

            libClonezilla.Utility.ThrowIfNoPartitions(containers, "list");

            containers
                .ForEach(container =>
                {
                    container.Partitions
                        .ForEach(partition =>
                        {
                            var partitionName = partition.PartitionName;

                            Log.Information($"[{container.ContainerName}] [{partitionName}] Retrieving a list of files.");

                            // With a warm cache, listing needs no extractor at all: opening the native
                            // 7z workers makes 7z scan the filesystem through the compressed stream,
                            // which cost warm xz listings ~100 s for nothing (TEST_ANALYSIS.md #3).
                            // Only a cache miss constructs one (to enumerate; disposed straight after).
                            IExtractor? extractor = null;
                            try
                            {
                                List<ArchiveEntry> filesInArchive;
                                var cachedList = partition.PartitionCache?.GetFileList();
                                if (cachedList != null)
                                {
                                    filesInArchive = cachedList
                                        .Where(entry => !Path.GetFileName(entry.Path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                                        .ToList();
                                }
                                else
                                {
                                    // Listing only enumerates - one worker is enough (mounting uses several for
                                    // concurrent reads).
                                    extractor = DetermineExtractor.FindExtractor(
                                        partition.CreateReaderFactory(),
                                        DetermineExtractor.ListingWorkerCount);

                                    if (extractor is IFileListProvider fileListProvider)
                                    {
                                        filesInArchive = partition.GetFilesInPartition(fileListProvider).ToList();
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

            //the records above are the program's real output; make sure they are all on the wire before we
            //exit (stdout is UTF-8 and separate from the stderr log stream) (gh review F47).
            Console.Out.Flush();
        }

        private static ReturnCode MountAsImageFiles(MountAsImageFiles mountAsImageOptions)
        {
            if (mountAsImageOptions.InputPaths == null) throw new Exception($"{nameof(mountAsImageOptions.InputPaths)} not specified.");

            if (!TryResolveMountPoint(mountAsImageOptions.MountPoint, out var mountPoint, out var mountPointError))
            {
                Log.Error(mountPointError);
                return ReturnCode.InvalidArguments;
            }

            //only an auto-chosen drive letter may silently fall back to another; a user-chosen one must not.
            //A null mount point stays null: OnDemandVFS picks a free letter at mount time.
            var mountPointWasAutoSelected = mountPoint == null;

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
                mountAsImageOptions.ProcessTrailingNulls);

            libClonezilla.Utility.PopulateVFS(vfs, vfs.Value.RootFolder.Value, containers, DesiredContent.ImageFiles);

            //the fallback can land the mount on a different letter than requested
            var mountedAt = vfs.Value.RootFolder.Value.MountPoint;
            Log.Information($"Mounting complete. Mounted to: {mountedAt}");
            if (!mountAsImageOptions.NoExplorer) Process.Start("explorer.exe", mountedAt);

            ServeUntilEnter(vfs.Value);
            return ReturnCode.Success;
        }

        private static ReturnCode MountAsFiles(MountAsFiles mountAsFilesOptions)
        {
            if (mountAsFilesOptions.InputPaths == null) throw new Exception($"{nameof(mountAsFilesOptions.InputPaths)} not specified.");

            if (!TryResolveMountPoint(mountAsFilesOptions.MountPoint, out var mountPoint, out var mountPointError))
            {
                Log.Error(mountPointError);
                return ReturnCode.InvalidArguments;
            }

            //only an auto-chosen drive letter may silently fall back to another; a user-chosen one must not.
            //A null mount point stays null: OnDemandVFS picks a free letter at mount time.
            var mountPointWasAutoSelected = mountPoint == null;

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
                mountAsFilesOptions.ProcessTrailingNulls);

            libClonezilla.Utility.PopulateVFS(vfs, vfs.Value.RootFolder.Value, containers, DesiredContent.Files);

            //the fallback can land the mount on a different letter than requested
            var mountedAt = vfs.Value.RootFolder.Value.MountPoint;
            Log.Information($"Mounting complete. Mounted to: {mountedAt}");
            if (!mountAsFilesOptions.NoExplorer) Process.Start("explorer.exe", mountedAt);

            ServeUntilEnter(vfs.Value);
            return ReturnCode.Success;
        }

        //Serve until Enter (or Ctrl+C), then unmount cleanly. Process exit drops the volume anyway, but only a
        //clean unmount removes a FOLDER mount point's reparse point; otherwise the folder is left as a dead mount
        //point until the next mount clears it (gh review F51).
        private static void ServeUntilEnter(IVFS vfs)
        {
            Console.CancelKeyPress += (_, _) => vfs.Unmount();

            Console.WriteLine("Running. Press Enter to exit.");
            if (Console.ReadLine() == null)
            {
                //No console input: stdin is at end of file (Task Scheduler, a service wrapper, a redirected launch).
                //ReadLine returned at once, and the mount used to end straight after mounting with exit code 0. Serve
                //until Ctrl+C or the process is stopped instead (gh review F54).
                Log.Information("stdin is at end of file, so Enter cannot end the mount; serving until Ctrl+C or the process is stopped.");
                Thread.Sleep(Timeout.Infinite);
            }

            vfs.Unmount();
        }

        //Validates and normalises a user-supplied --mount value before anything is loaded or mounted (gh review
        //F51). A drive letter written L, L:, L:\ or L:/ becomes "L:\" (a bare "L:" used to get past the live-volume
        //probe, mount, then poll a relative path for 30 s before failing). Anything else is a folder mount point,
        //which Dokan requires to be an existing, empty folder on NTFS - checked here so a bad one fails at once
        //with the reason. Null (auto-select a letter at mount time) passes through.
        private static bool TryResolveMountPoint(string? requested, out string? mountPoint, out string error)
        {
            mountPoint = null;
            error = "";
            if (requested == null) return true;

            var trimmed = requested.Trim();
            if (trimmed.Length is >= 1 and <= 3
                && char.IsAsciiLetter(trimmed[0])
                && (trimmed.Length < 2 || trimmed[1] == ':')
                && (trimmed.Length < 3 || trimmed[2] is '\\' or '/'))
            {
                mountPoint = $"{char.ToUpperInvariant(trimmed[0])}:\\";
                return true;
            }

            const string expected = @"A mount point must be a drive letter (e.g. L:\) or an existing, empty folder on an NTFS volume.";
            try
            {
                var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));

                //A DEAD mount point (left by a run that was killed before it could unmount) is accepted: the mount
                //clears it back to the empty folder it was. A live mount point or a link is refused.
                var deadMountPoint = libDokan.Utility.IsDeadVolumeMountPoint(folder);

                if (!Directory.Exists(folder))
                    error = $"The mount point folder '{folder}' does not exist. {expected}";
                else if (!deadMountPoint && File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint))
                    error = $"The mount point folder '{folder}' is already a mount point or link. {expected}";
                else if (!deadMountPoint && Directory.EnumerateFileSystemEntries(folder).Any())
                    error = $"The mount point folder '{folder}' is not empty. {expected}";
                else if (!libCommon.Utility.IsOnNTFS(folder))
                    error = $"The mount point folder '{folder}' is not on an NTFS volume. {expected}";
                else
                    mountPoint = folder;
            }
            catch (Exception ex)
            {
                error = $"'{requested}' cannot be used as a mount point ({ex.Message}). {expected}";
            }

            return mountPoint != null;
        }

        private static void ExtractPartitionImage(ExtractPartitionImage extractPartitionImageOptions)
        {
            if (extractPartitionImageOptions.InputPaths == null) throw new Exception($"{nameof(extractPartitionImageOptions.InputPaths)} not specified.");
            if (extractPartitionImageOptions.OutputFolder == null) throw new Exception($"{nameof(extractPartitionImageOptions.OutputFolder)} not specified.");

            //headless for every input: nothing here mounts (gh review F46)
            var containers = PartitionContainer.FromPaths(
                                extractPartitionImageOptions.InputPaths.ToList(),
                                CacheFolder,
                                extractPartitionImageOptions.PartitionsToExtract.ToList(),
                                false,
                                extractPartitionImageOptions.ProcessTrailingNulls);

            //created only now that the inputs have loaded, so a bad input leaves no empty folder behind (gh review F58)
            Directory.CreateDirectory(extractPartitionImageOptions.OutputFolder);

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

        private static ReturnCode ExtractFiles(ExtractFiles extractOptions)
        {
            if (extractOptions.InputPaths == null) throw new Exception($"{nameof(extractOptions.InputPaths)} not specified.");

            var inputPaths = extractOptions.InputPaths.ToList();
            var outputRoot = ResolveExtractOutputRoot(extractOptions.OutputFolder, inputPaths);
            var outputRootFull = Path.GetFullPath(outputRoot);

            var filter = new PathGlobFilter(extractOptions.Include, extractOptions.Exclude);

            //Headless for every input: extract enumerates and copies streams in-process and never mounts, so Dokan
            //is not needed. (Compressed image files used to be decompressed through a temp mount - gh review F46.)
            var containers = PartitionContainer.FromPaths(
                                inputPaths,
                                CacheFolder,
                                extractOptions.PartitionsToExtract.ToList(),
                                true,   //random access: we seek to scattered individual files
                                extractOptions.ProcessTrailingNulls);

            //created only now that the inputs have loaded, so a bad input leaves no empty folder behind (gh review F58)
            Directory.CreateDirectory(outputRoot);

            long totalFiles = 0;
            long totalBytes = 0;
            long failedFiles = 0;

            //Destinations already claimed, case-insensitively, across the whole extract. Shared (not
            //per-partition) so --flatten collisions between two partitions are caught too (gh review F11).
            var seenDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            containers.ForEach(container =>
            {
                container.Partitions.ForEach(partition =>
                {
                    var containerName = container.ContainerName;
                    var partitionName = partition.PartitionName;

                    //preserve-mode output nests under a partition-named folder; prefix with the container
                    //name too when several containers could otherwise collide on the same partition name.
                    var prefix = containers.Count == 1 ? partitionName : $"{containerName}.{partitionName}";

                    List<ArchiveEntry> SelectMatches(IEnumerable<ArchiveEntry> files) => files
                        .Where(e => !e.IsFolder)
                        .Where(e => !Path.GetFileName(e.Path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        .Where(e => IsExtractableToFile(e.Path))
                        .Where(e => filter.Matches(Path.Combine(containerName, partitionName, e.Path)))
                        .ToList();

                    IExtractor? extractor = null;
                    try
                    {
                        //With the partition's file list already cached, the matches are known before any native
                        //worker opens: nothing to extract means no extractor at all, and a few files need only a few
                        //workers. Each worker parses the filesystem on open, and all eight used to open even for a
                        //single file (gh review F35).
                        List<ArchiveEntry>? matches = null;
                        var cachedList = partition.PartitionCache?.GetFileList();
                        if (cachedList != null)
                        {
                            matches = SelectMatches(cachedList);
                            if (matches.Count == 0)
                            {
                                Log.Information($"[{containerName}] [{partitionName}] No files matched.");
                                return;
                            }
                        }

                        //At most MountWorkerCount (not ListingWorkerCount): the same pool serves the file list AND
                        //the parallel content reads below. The workers read through the same lock-free cursors
                        //as the mount path (a SharedStream view here made them decode one file at a time - gh
                        //review F7).
                        extractor = DetermineExtractor.FindExtractor(
                            partition.CreateReaderFactory(),
                            Math.Min(DetermineExtractor.MountWorkerCount, matches?.Count ?? int.MaxValue));

                        if (extractor is not IFileListProvider fileListProvider)
                        {
                            Log.Error($"[{containerName}] [{partitionName}] Could not find a suitable extractor for this partition. Skipping.");
                            return;
                        }

                        //not cached: list through the extractor, which also caches the list for next time, as list does
                        matches ??= SelectMatches(partition.GetFilesInPartition(fileListProvider));

                        if (matches.Count == 0)
                        {
                            Log.Information($"[{containerName}] [{partitionName}] No files matched.");
                            return;
                        }

                        Log.Information($"[{containerName}] [{partitionName}] Extracting {matches.Count} file(s) to: {outputRoot}");

                        var localExtractor = extractor;

                        //Each iteration owns its own PooledNativeItemStream and reads it on one thread; the
                        //worker pool serialises native access. This is the concurrency model the mount path
                        //exercises under the ConcurrentBleedStress gate.
                        Parallel.ForEach(
                            matches,
                            new ParallelOptions { MaxDegreeOfParallelism = DetermineExtractor.MountWorkerCount },
                            entry =>
                            {
                                var desired = extractOptions.Flatten
                                    ? Path.Combine(outputRoot, Path.GetFileName(entry.Path))
                                    : Path.Combine(outputRoot, prefix, entry.Path);

                                //Never write outside the output folder. Entry paths come straight from the
                                //archive; a rooted path or a ".." segment would otherwise let Path.Combine /
                                //File.Create escape the root. No supported 7-Zip handler emits such a name
                                //today, but this is the cheap backstop (gh review F44).
                                if (!IsInsideRoot(Path.GetFullPath(desired), outputRootFull))
                                {
                                    Interlocked.Increment(ref failedFiles);
                                    Log.Warning($"[{containerName}] [{partitionName}] Refusing to extract '{entry.Path}': it resolves outside the output folder.");
                                    return;
                                }

                                var destination = ResolveUniqueDestination(desired, seenDestinations);
                                if (!string.Equals(destination, desired, StringComparison.Ordinal))
                                    Log.Warning($"[{containerName}] [{partitionName}] Output name collision for '{entry.Path}'; writing as '{Path.GetFileName(destination)}' so it does not overwrite another extracted file.");

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

            //a typo in an --include otherwise just extracts less, silently (gh review F55)
            foreach (var pattern in filter.UnmatchedIncludes())
            {
                Log.Warning($"--include \"{pattern}\" matched no files.");
            }

            Log.Information($"Extracted {totalFiles} file(s), {totalBytes.BytesToString()}, to: {outputRoot}");
            if (failedFiles > 0)
            {
                //Non-zero exit so a caller/script can tell a partial extract from a clean one (gh review F17).
                Log.Warning($"{failedFiles} file(s) could not be extracted (see warnings above).");
                return ReturnCode.PartialFailure;
            }

            return ReturnCode.Success;
        }

        //Both extract modes can map two archive entries onto one host path: --flatten collapses different
        //directories onto a single filename, and preserve mode collides when a case-SENSITIVE source
        //filesystem (ext4/xfs) holds names differing only in case, which NTFS treats as one file. Hand
        //back a unique path (suffixing " (2)", " (3)", ...) so the second writer never silently overwrites
        //the first (gh review F11). Thread-safe: called from the parallel extract loop.
        private static string ResolveUniqueDestination(string destination, HashSet<string> taken)
        {
            lock (taken)
            {
                if (taken.Add(destination)) return destination;

                var dir = Path.GetDirectoryName(destination) ?? "";
                var stem = Path.GetFileNameWithoutExtension(destination);
                var ext = Path.GetExtension(destination);
                for (var i = 2; ; i++)
                {
                    var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (taken.Add(candidate)) return candidate;
                }
            }
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
            if (Path.IsPathRooted(archivePath)) return false;   //a rooted archive name would escape the output root
            return true;
        }

        //True when fullPath is the output root itself or lies within it. Both arguments must already be
        //fully-qualified. Guards the extract destination against archive names that resolve outside the
        //output folder (gh review F44).
        private static bool IsInsideRoot(string fullPath, string fullRoot)
        {
            if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)) return true;

            var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
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
                : StripImageExtensions(Path.GetFileName(firstInput));
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "extracted";

            var candidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), baseName));

            //Never the input itself, a folder inside it, or an existing FILE: stripping one extension turned
            //sda.img.gz into sda.img, which is often a real file sitting right next to it (gh review F58).
            var inputFull = Path.GetFullPath(firstInput);
            if (candidate.Equals(inputFull, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(inputFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(candidate))
            {
                candidate = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), baseName + "-extracted"));
            }

            return candidate;
        }

        //Compression, split-volume and raw-image extensions, peeled repeatedly from a file name to name its default
        //extract folder: sda.img.gz -> sda, sda1.ntfs-ptcl-img.gz.aa -> sda1.ntfs-ptcl-img.
        static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".gz", ".bz2", ".xz", ".zst", ".zstd", ".lz4", ".lzip", ".lz", ".uncomp", ".aa", ".img", ".image", ".dd", ".raw",
        };

        static string StripImageExtensions(string fileName)
        {
            var name = fileName;
            while (ImageExtensions.Contains(Path.GetExtension(name)) && Path.GetFileNameWithoutExtension(name).Length > 0)
            {
                name = Path.GetFileNameWithoutExtension(name);
            }
            return name;
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
    }
}
