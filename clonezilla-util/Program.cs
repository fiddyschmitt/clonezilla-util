using clonezilla_util.CL.Verbs;
using clonezilla_util.Extractors;
using clonezilla_util.VFS;
using CommandLine;
using DokanNet;
using lib7Zip;
using libClonezilla;
using libClonezilla.Cache;
using libClonezilla.PartitionContainers;
using libClonezilla.Partitions;
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
using System.Threading;
using System.Threading.Tasks;

namespace clonezilla_util
{
    class Program
    {
        const string PROGRAM_NAME = "clonezilla-util";
        const string PROGRAM_VERSION = "1.4.1";

        private enum ReturnCode
        {
            Success = 0,
            InvalidArguments = 1,
            GeneralException = 2,
        }

        public static string CacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");

        static int Main(string[] args)
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

            var root = new Folder("", null);

            var containers = PartitionContainer.FromPaths(
                                listContentsOptions.InputPaths.ToList(),
                                CacheFolder,
                                listContentsOptions.PartitionsToInspect.ToList(),
                                true)
                            .OrderBy(container => container.ContainerName)
                            .ToList();

            libClonezilla.Utility.MountPartitionsAsImageFiles(
                PROGRAM_NAME,
                containers,
                mountPoint,
                root,
                root);

            var partitions = containers
                                .SelectMany(container => container.Partitions)
                                .ToList();

            partitions
                .ForEach(partition =>
                {
                    var partitionName = partition.Name;

                    Log.Information($"[{partition.Container.ContainerName}] [{partitionName}] Retrieving a list of files.");

                    var filesInArchive = partition.GetFilesInPartition();

                    foreach (var archiveEntry in filesInArchive)
                    {
                        var filenameIncludingPartition = Path.Combine(partition.Container.ContainerName, partitionName, archiveEntry.Path);

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
        }

        private static void MountAsImageFiles(MountAsImageFiles mountAsImageOptions)
        {
            if (mountAsImageOptions.InputPaths == null) throw new Exception($"{nameof(mountAsImageOptions.InputPaths)} not specified.");
            if (mountAsImageOptions.MountPoint == null) mountAsImageOptions.MountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var root = new Folder("", null);
            var mountPoint = mountAsImageOptions.MountPoint;

            var containers = PartitionContainer.FromPaths(
                                mountAsImageOptions.InputPaths.ToList(),
                                CacheFolder,
                                mountAsImageOptions.PartitionsToMount.ToList(),
                                true);

            libClonezilla.Utility.MountPartitionsAsImageFiles(
                PROGRAM_NAME,
                containers,
                mountPoint,
                root,
                root);

            Log.Information($"Mounting complete. Mounted to: {mountAsImageOptions.MountPoint}");
            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        private static void MountAsFiles(MountAsFiles mountAsFilesOptions)
        {
            if (mountAsFilesOptions.InputPaths == null) throw new Exception($"{nameof(mountAsFilesOptions.InputPaths)} not specified.");
            if (mountAsFilesOptions.MountPoint == null) mountAsFilesOptions.MountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var containers = PartitionContainer.FromPaths(
                            mountAsFilesOptions.InputPaths.ToList(),
                            CacheFolder,
                            mountAsFilesOptions.PartitionsToMount.ToList(),
                            true);

            var root = new Folder("", null);

            //create a hidden folder for the images, so that we can interogate them using 7z.exe
            var imagesRootFolder = $"images {Guid.NewGuid()}";
            var imagesRoot = new Folder(imagesRootFolder, root)
            {
                Hidden = true
            };

            libClonezilla.Utility.MountPartitionsAsImageFiles(
                 PROGRAM_NAME,
                 containers,
                mountAsFilesOptions.MountPoint,
                imagesRoot,
                root);

            //not adding the files directly to root, because we want to present them all at once at the very end
            var containerFolders = new List<Folder>();

            containers
                .ForEach(container =>
                {
                    var containerFolder = new Folder(container.ContainerName, null);
                    containerFolders.Add(containerFolder);

                    container
                        .Partitions
                        .ForEach(partition =>
                        {
                            var partitionName = partition.Name;

                            Log.Information($"[{container.ContainerName}] [{partitionName}] Retrieving a list of files.");

                            var filesInArchive = partition.GetFilesInPartition().ToList();

                            //Do a performance test. If the archive can be opened quickly, then use 7z.exe which is slow but reliable. If it takes a long time, then use 7zFM which is fast but less reliable.

                            //pick a small file
                            var testFile = filesInArchive
                                                .FirstOrDefault(file => file.Size < 1024 * 1024);

                            if (partition.PhysicalImageFilename == null) throw new Exception($"{nameof(partition.PhysicalImageFilename)} is null. The file has not yet been initialised in the Virtual File System.");

                            bool use7z;

                            if (testFile == null)
                            {
                                use7z = true;
                            }
                            else
                            {
                                Log.Debug($"[{container.ContainerName}] [{partitionName}] Running a performance test to determine the optimal way to extract files from this image.");

                                var testStart = DateTime.Now;
                                var testExtractor = new ExtractorUsing7z(partition.PhysicalImageFilename);
                                var testStream = testExtractor.Extract(testFile.Path);
                                testStream.CopyTo(Stream.Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER);

                                var testDuration = DateTime.Now - testStart;

                                Log.Debug($"[{container.ContainerName}] [{partitionName}] Nominal file read in {testDuration.TotalSeconds:N1} seconds.");

                                if (testDuration.TotalSeconds < 10)
                                {
                                    use7z = true;
                                }
                                else
                                {
                                    use7z = false;
                                }
                            }

                            IExtractor extractor;
                            if (use7z)
                            {
                                Log.Information($"[{container.ContainerName}] [{partitionName}] 7z.exe will be used to extract files from this partition.");

                                //Extractor which uses 7z.exe.
                                //It runs the process and returns its stdout straight away, so it's non-blocking.
                                //Works fine, except it's too slow when extracting from a large archive, causing explorer.exe to assume the file isn't available.
                                extractor = new ExtractorUsing7z(partition.PhysicalImageFilename);

                                //This actually causes errors for FLP, because FLP uses more threads than are being served
                                /*
                                var extractors = new List<IExtractor>();
                                for (int i = 0; i < 4; i++)
                                {
                                    var newExtractor = new ExtractorUsing7z(realImageFile);
                                    extractors.Add(newExtractor);
                                }

                                extractor = new MultiExtractor(extractors, true);
                                */
                            }
                            else
                            {
                                Log.Information($"[{container.ContainerName}] [{partitionName}] 7zFM.exe will be used to extract files from this partition.");

                                //Extractor which uses the 7-Zip File Manager
                                //Opens the archive here, up front. Subsequent extracts are quick                    
                                var extractors = new List<IExtractor>();
                                for (int i = 0; i < 4; i++)
                                {
                                    var newExtractor = new ExtractorUsing7zFM(partition.PhysicalImageFilename);
                                    extractors.Add(newExtractor);
                                }

                                extractor = new MultiExtractor(extractors, true);
                            }

                            //Extractor which uses the SevenZipExtractor library from NuGet
                            //Was hoping that we could get SevenZipExtractor to load the archive (which takes time) in the constructor, so that it would be more responsive when asked to extract a file from it. But it still takes too long, causing an explorer.exe timeout.
                            /*
                            {
                                var partitionDetails = partitionContainer
                                                    .Partitions
                                                    .FirstOrDefault(partition => partition.Name.Equals(partitionName));

                                if (partitionDetails == null) throw new Exception($"Could not load details for {partitionName}.");
                                for (int i = 0; i < 4; i++)
                                {
                                    //don't use - seems to return incorrect file content
                                    var newExtractor = new ExtractorUsingSevenZipExtractorLibrary(partitionDetails.FullPartitionImage);

                                    //doesn't seem to like being run concurrently. Likely because it uses the native 7z.dll
                                    //var newExtractor = new ExtractorUsingSevenZipExtractorLibrary(realImageFile);

                                    extractors.Add(newExtractor);
                                }
                            }
                            */




                            //this layer caches streams as they're created, so that they are extracted only once
                            extractor = new CachedExtractor(extractor);

                            //this layer makes the extractor thread-safe by wrapping each stream in a Stream.Synchronized()
                            extractor = new SynchronisedExtractor(extractor);

                            var partitionRoot = new Folder(partitionName, containerFolder);
                            CreateTree(container.ContainerName, partitionName, partitionRoot, filesInArchive, extractor);
                        });
                });

            //now that the file list is complete, we can add it to the root.
            if (containers.Count == 1)
            {
                var partitionFolders = containerFolders
                                    .SelectMany(containerFolder => containerFolder.Children)
                                    .ToList();

                root.AddChildren(partitionFolders);
            }
            else
            {
                root.AddChildren(containerFolders);
            }


            Log.Information($"Mounting complete. Mounted to: {mountAsFilesOptions.MountPoint}");
            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        protected static void CreateTree(string containerName, string partitionName, Folder root, List<ArchiveEntry> allEntries, IExtractor extractor)
        {
            Log.Debug($"[{containerName}] [{partitionName}] Building folder dictionary.");
            var entries = allEntries
                                .Select(archiveEntry =>
                                {
                                    FileSystemEntry newEntry;

                                    string containingFolder;
                                    string pathInArchive;
                                    if (archiveEntry.IsFolder)
                                    {
                                        var virtualFolderPath = archiveEntry.Path;
                                        if (virtualFolderPath.EndsWith(@"\."))
                                        {
                                            virtualFolderPath = virtualFolderPath.Replace(@"\.", "");
                                        }

                                        var name = Path.GetFileName(virtualFolderPath);
                                        newEntry = new Folder(name, null)
                                        {
                                            Created = archiveEntry.Created,
                                            Modified = archiveEntry.Modified,
                                            Accessed = archiveEntry.Accessed,
                                        };

                                        containingFolder = Path.GetDirectoryName(virtualFolderPath) ?? throw new Exception($"Could not get parent directory for: {virtualFolderPath}");
                                        pathInArchive = virtualFolderPath;
                                    }
                                    else
                                    {
                                        var virtualFilePath = archiveEntry.Path;

                                        var name = Path.GetFileName(virtualFilePath);
                                        newEntry = new SevenZipBackedFileEntry(archiveEntry, null, extractor);

                                        containingFolder = Path.GetDirectoryName(virtualFilePath) ?? throw new Exception($"Could not get parent directory for: {virtualFilePath}");
                                        pathInArchive = virtualFilePath;
                                    }

                                    return new
                                    {
                                        NewEntry = newEntry,
                                        ContainingFolder = containingFolder,
                                        PathInArchive = pathInArchive
                                    };
                                })
                                .ToList();

            var folderLookup = entries
                                .Where(entry => entry.NewEntry is Folder)
                                .ToDictionary(
                                    entry => entry.PathInArchive,
                                    entry => entry.NewEntry as Folder);

            folderLookup[""] = root;

            var pathLookup = entries
                                .ToDictionary(
                                    vfsEntry => vfsEntry.NewEntry,
                                    vfsEntry => vfsEntry.ContainingFolder);

            Log.Debug($"[{containerName}] [{partitionName}] Establishing parent/child relationships.");
            entries
                .Select(entry => entry.NewEntry)
                .Cast<FileSystemEntry>()
                .ForEach(fileSystemEntry =>
                {
                    var parentFolder = pathLookup[fileSystemEntry];

                    var parent = folderLookup[parentFolder];

                    if (parent == null) throw new Exception($"Parent not found for: {fileSystemEntry.FullPath}");

                    fileSystemEntry.Parent = parent;
                });
        }

        private static void ExtractPartitionImage(ExtractPartitionImage extractPartitionImageOptions)
        {
            if (extractPartitionImageOptions.InputPaths == null) throw new Exception($"{nameof(extractPartitionImageOptions.InputPaths)} not specified.");
            if (extractPartitionImageOptions.OutputFolder == null) throw new Exception($"{nameof(extractPartitionImageOptions.OutputFolder)} not specified.");

            if (!Directory.Exists(extractPartitionImageOptions.OutputFolder))
            {
                Directory.CreateDirectory(extractPartitionImageOptions.OutputFolder);
            }

            var containers = PartitionContainer.FromPaths(
                                extractPartitionImageOptions.InputPaths.ToList(),
                                CacheFolder,
                                extractPartitionImageOptions.PartitionsToExtract.ToList(),
                                false);

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
                                outputFilename = Path.Combine(extractPartitionImageOptions.OutputFolder, $"{partition.Name}.img");
                            }
                            else
                            {
                                outputFilename = Path.Combine(extractPartitionImageOptions.OutputFolder, $"{container.ContainerName}.{partition.Name}.img");
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
