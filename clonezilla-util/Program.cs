﻿using clonezilla_util.CL.Verbs;
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
        const string PROGRAM_VERSION = "1.2.1";

        private enum ReturnCode
        {
            Success = 0,
            InvalidArguments = 1,
            GeneralException = 2,
        }


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
            string cacheFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");

            ClonezillaCacheManager? clonezillaCacheManager = null;
            if (obj is BaseVerb baseVerb)
            {
                if (baseVerb.InputPath == null) throw new Exception($"{nameof(baseVerb.InputPath)} not specified.");
                clonezillaCacheManager = new(baseVerb.InputPath, cacheFolder);
            }
            if (clonezillaCacheManager == null) throw new Exception($"Did not create ClonezillaCacheManager.");

            switch (obj)
            {
                case ExtractPartitionImage extractPartitionImageOptions:

                    ExtractPartitionImage(extractPartitionImageOptions, clonezillaCacheManager);
                    break;

                case MountAsImage mountAsImageOptions:

                    MountAsImage(mountAsImageOptions, clonezillaCacheManager);
                    break;

                case MountAsFiles mountAtFilesOptions:

                    MountAsFiles(mountAtFilesOptions, clonezillaCacheManager);
                    break;

                case ListContents listContentsOptions:

                    ListContents(listContentsOptions, clonezillaCacheManager);
                    break;
            }
        }

        static IEnumerable<ArchiveEntry> GetFilesInPartition(string realImageFile, IPartitionCache? partitionCache)
        {
            IEnumerable<ArchiveEntry>? archiveFiles = partitionCache?.GetFileList();

            bool saveToCache = false;
            if (archiveFiles == null)
            {
                archiveFiles = SevenZipUtility.GetArchiveEntries(realImageFile, false);
                saveToCache = true;
            };

            var fullListOfFiles = new List<ArchiveEntry>();

            foreach (var archiveEntry in archiveFiles)
            {
                if (Path.GetFileName(archiveEntry.Path).Equals("desktop.ini", StringComparison.CurrentCultureIgnoreCase)) continue; //a micro-optimisation to stop Windows from requesting this file and causing a lot of unecessary IO

                fullListOfFiles.Add(archiveEntry);
                yield return archiveEntry;
            }

            if (saveToCache)
            {
                partitionCache?.SetFileList(fullListOfFiles);
            }
        }

        private static void ListContents(ListContents listContentsOptions, ClonezillaCacheManager clonezillaCacheManager)
        {
            if (listContentsOptions.InputPath == null) throw new Exception($"{nameof(listContentsOptions.InputPath)} not specified.");

            var partitionContainerType = IPartitionContainer.FromPath(listContentsOptions.InputPath);

            IPartitionContainer partitionContainer = partitionContainerType switch
            {
                PartitionContainerType.ClonezillaFolder => new ClonezillaImage(listContentsOptions.InputPath, clonezillaCacheManager, listContentsOptions.PartitionsToInspect, true),
                PartitionContainerType.PartcloneFile => new PartcloneFile(listContentsOptions.InputPath, true),
                _ => throw new NotImplementedException()
            };

            var mountPoint = libDokan.Utility.GetAvailableDriveLetter();
            var root = new Folder("");

            AddPartitionImagesToVirtualFolder(partitionContainer.Partitions, mountPoint, root, "");

            var vfs = new DokanVFS(PROGRAM_NAME, root);
            Task.Factory.StartNew(() => vfs.Mount(mountPoint, DokanOptions.WriteProtection, new DokanNet.Logging.NullLogger()));
            WaitForMountPointToBeAvailable(mountPoint);

            //use 7z to get a list of files
            var imageFiles = Directory.GetFiles(mountPoint, "*", SearchOption.AllDirectories).ToList();

            imageFiles
                .ForEach(realImageFile =>
                {
                    var partitionName = Path.GetFileNameWithoutExtension(realImageFile);
                    Log.Information($"[{partitionName}] Retrieving a list of files.");

                    var partitionCache = clonezillaCacheManager.GetPartitionCache(partitionName);

                    var filesInArchive = GetFilesInPartition(realImageFile, partitionCache);

                    foreach (var archiveEntry in filesInArchive)
                    {
                        var filenameIncludingPartition = Path.Combine(partitionName, archiveEntry.Path);

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

        private static void MountAsImage(MountAsImage mountAsImageOptions, ClonezillaCacheManager clonezillaCacheManager)
        {
            if (mountAsImageOptions.InputPath == null) throw new Exception($"{nameof(mountAsImageOptions.InputPath)} not specified.");
            if (mountAsImageOptions.MountPoint == null) mountAsImageOptions.MountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var partitionContainerType = IPartitionContainer.FromPath(mountAsImageOptions.InputPath);

            IPartitionContainer partitionContainer = partitionContainerType switch
            {
                PartitionContainerType.ClonezillaFolder => new ClonezillaImage(mountAsImageOptions.InputPath, clonezillaCacheManager, mountAsImageOptions.PartitionsToExtract, true),
                PartitionContainerType.PartcloneFile => new PartcloneFile(mountAsImageOptions.InputPath, true),
                _ => throw new NotImplementedException()
            };

            var root = new Folder("");
            AddPartitionImagesToVirtualFolder(partitionContainer.Partitions, mountAsImageOptions.MountPoint, root, "");

            var vfs = new DokanVFS(PROGRAM_NAME, root);
            Task.Factory.StartNew(() =>
            {
                vfs.Mount(mountAsImageOptions.MountPoint, DokanOptions.WriteProtection, new DokanNet.Logging.NullLogger());
            });

            Log.Information($"Mounting complete. Mounted to: {mountAsImageOptions.MountPoint}");
            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        private static void MountAsFiles(MountAsFiles mountAsFilesOptions, ClonezillaCacheManager clonezillaCacheManager)
        {
            if (mountAsFilesOptions.InputPath == null) throw new Exception($"{nameof(mountAsFilesOptions.InputPath)} not specified.");
            if (mountAsFilesOptions.MountPoint == null) mountAsFilesOptions.MountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var partitionContainerType = IPartitionContainer.FromPath(mountAsFilesOptions.InputPath);

            IPartitionContainer partitionContainer = partitionContainerType switch
            {
                PartitionContainerType.ClonezillaFolder => new ClonezillaImage(mountAsFilesOptions.InputPath, clonezillaCacheManager, mountAsFilesOptions.PartitionsToExtract, true),
                PartitionContainerType.PartcloneFile => new PartcloneFile(mountAsFilesOptions.InputPath, true),
                _ => throw new NotImplementedException()
            };

            var root = new Folder("");

            //create a hidden folder for the images, so that we can interogate them using 7z.exe
            var imagesRootFolder = $"images {Guid.NewGuid()}";

            AddPartitionImagesToVirtualFolder(partitionContainer.Partitions, mountAsFilesOptions.MountPoint, root, imagesRootFolder);

            var imagesRoot = root.CreateOrRetrieveFolder(imagesRootFolder);
            imagesRoot.Hidden = true;

            var vfs = new DokanVFS(PROGRAM_NAME, root);
            Task.Factory.StartNew(() =>
            {
                vfs.Mount(mountAsFilesOptions.MountPoint, DokanOptions.WriteProtection, 64, new DokanNet.Logging.NullLogger());
            });
            WaitForMountPointToBeAvailable(mountAsFilesOptions.MountPoint);

            //use 7z to get a list of files (or load them from cache)
            var realImagesFolder = Path.Combine(mountAsFilesOptions.MountPoint, imagesRootFolder);
            var imageFiles = Directory.GetFiles(realImagesFolder, "*", SearchOption.AllDirectories).ToList();

            //not adding the files directly to root, because we want to present them all at once at the very end
            var filesRoot = new Folder("");

            imageFiles
                .ForEach(realImageFile =>
                {
                    var partitionName = Path.GetFileNameWithoutExtension(realImageFile);
                    Log.Information($"[{partitionName}] Retrieving a list of files.");

                    var partitionCache = clonezillaCacheManager.GetPartitionCache(partitionName);

                    var filesInArchive = GetFilesInPartition(realImageFile, partitionCache).ToList();

                    //Do a performance test. If the archive can be opened quickly, then use 7z.exe which is slow but reliable. If it takes a long time, then use 7zFM which is fast but less reliable.

                    //pick a small file
                    var testFile = filesInArchive
                                        .FirstOrDefault(file => file.Size < 1024 * 1024);

                    bool use7z;

                    if (testFile == null)
                    {
                        use7z = true;
                    }
                    else
                    {
                        Log.Debug($"[{partitionName}] Running a performance test to determine the optimal way to extract files from this image.");

                        var testStart = DateTime.Now;
                        var testExtractor = new ExtractorUsing7z(realImageFile);
                        var testStream = testExtractor.Extract(testFile.Path);
                        testStream.CopyTo(Stream.Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER);

                        var testDuration = DateTime.Now - testStart;

                        Log.Debug($"[{partitionName}] Nominal file read in {testDuration.TotalSeconds:N1} seconds.");

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
                        Log.Information($"[{partitionName}] 7z.exe will be used to extract files from this partition.");

                        //Extractor which uses 7z.exe.
                        //It runs the process and returns its stdout straight away, so it's non-blocking.
                        //Works fine, except it's too slow when extracting from a large archive, causing explorer.exe to assume the file isn't available.
                        extractor = new ExtractorUsing7z(realImageFile);

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
                        Log.Information($"[{partitionName}] 7zFM.exe will be used to extract files from this partition.");

                        //Extractor which uses the 7-Zip File Manager
                        //Opens the archive here, up front. Subsequent extracts are quick                    
                        var extractors = new List<IExtractor>();
                        for (int i = 0; i < 4; i++)
                        {
                            var newExtractor = new ExtractorUsing7zFM(realImageFile);
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

                    var partitionRoot = new Folder(partitionName);

                    CreateTree(partitionName, partitionRoot, filesInArchive, extractor);

                    filesRoot.Children.Add(partitionRoot);
                });

            //now that the file list is complete, we can add it to the root.
            root.Children.AddRange(filesRoot.Children);

            Log.Information($"Mounting complete. Mounted to: {mountAsFilesOptions.MountPoint}");
            Console.WriteLine("Running. Press Enter to exit.");
            Console.ReadLine();
        }

        protected static void CreateTree(string partitionName, Folder root, List<ArchiveEntry> allEntries, IExtractor extractor)
        {
            Log.Debug($"[{partitionName}] Building folder dictionary.");
            var folders = allEntries
                                .Where(archiveEntry => archiveEntry.IsFolder)
                                .OrderBy(archiveEntry => archiveEntry.Path)
                                .Select(archiveEntry =>
                                {
                                    var virtualFolderPath = archiveEntry.Path;
                                    if (virtualFolderPath.EndsWith(@"\."))
                                    {
                                        virtualFolderPath = virtualFolderPath.Replace(@"\.", "");
                                    }
                                    virtualFolderPath = Path.Combine(partitionName, virtualFolderPath);

                                    var name = Path.GetFileName(virtualFolderPath);
                                    var newFolder = new Folder(name)
                                    {
                                        Created = archiveEntry.Created,
                                        Modified = archiveEntry.Modified,
                                        Accessed = archiveEntry.Accessed,
                                        FullPath = virtualFolderPath,
                                    };

                                    return newFolder;
                                })
                                .ToList();

            Log.Debug($"[{partitionName}] Building file list.");
            var files = allEntries
                            .Where(archiveEntry => !archiveEntry.IsFolder)
                            .Select(archiveEntry =>
                            {
                                var virtualFilePath = Path.Combine(partitionName, archiveEntry.Path);

                                var name = Path.GetFileName(archiveEntry.Path);
                                var newFile = new SevenZipBackedFileEntry(archiveEntry, extractor)
                                {
                                    FullPath = virtualFilePath
                                };
                                return newFile;
                            })
                            .ToList();

            var folderLookup = folders
                                .ToDictionary(vfsEntry => vfsEntry.FullPath!);

            folderLookup[root.Name] = root;

            Log.Debug($"[{partitionName}] Establishing parent/child relationships.");
            folders
                .Cast<FileSystemEntry>()
                .Union(files)
                .ForEach(fileSystemEntry =>
                {
                    var parentFolder = Path.GetDirectoryName(fileSystemEntry.FullPath);
                    if (parentFolder == null) throw new Exception($"Could not derive parent for {fileSystemEntry.FullPath}");

                    var parent = folderLookup[parentFolder];

                    parent.Children.Add(fileSystemEntry);
                });
        }

        private static void WaitForMountPointToBeAvailable(string mountPoint)
        {
            Log.Information($"Waiting for {mountPoint} to be available.");
            while (true)
            {
                if (Directory.Exists(mountPoint))
                {
                    break;
                }
                Thread.Sleep(100);
            }
        }

        static void AddPartitionImagesToVirtualFolder(List<Partition> partitions, string mountPoint, Folder root, string virtualFolderPath)
        {
            partitions
                .ForEach(partition =>
                {
                    var virtualImageFilename = Path.Combine(virtualFolderPath, $"{partition.Name}.img");
                    var physicalImageFilename = Path.Combine(mountPoint, virtualImageFilename);

                    var virtualFileName = Path.GetFileName(virtualImageFilename);

                    var virtualFolder = root.CreateOrRetrieveFolder(virtualFolderPath);

                    var fileEntry = new StreamBackedFileEntry(
                        virtualFileName,
                        () =>
                        {
                            var stream = partition.FullPartitionImage;
                            return stream;
                        })
                    {
                        Created = DateTime.Now,
                        Accessed = DateTime.Now,
                        Modified = DateTime.Now,
                        Length = partition.FullPartitionImage.Length,
                    };

                    virtualFolder.Children.Add(fileEntry);
                });
        }

        private static void ExtractPartitionImage(ExtractPartitionImage extractPartitionImageOptions, ClonezillaCacheManager clonezillaCacheManager)
        {
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
