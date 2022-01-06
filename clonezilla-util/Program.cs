using clonezilla_util.CL.Verbs;
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
using libPartclone;
using Serilog;
using Serilog.Core;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
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
            }

            var fullListOfFiles = new List<ArchiveEntry>();

            foreach (var archiveEntry in archiveFiles)
            {
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

            var root = new Folder("");
            AddPartitionImagesToVirtualFolder(partitionContainer.Partitions, root, "");

            var mountPoint = libDokan.Utility.GetAvailableDriveLetter();

            var vfs = new DokanVFS(root, PROGRAM_NAME);
            Task.Factory.StartNew(() => vfs.Mount(mountPoint, DokanOptions.WriteProtection, new DokanNet.Logging.NullLogger()));
            WaitForMountPointToBeAvailable(mountPoint);

            Log.Information($"Retrieving a list of files.");

            //use 7z to get a list of files
            var imageFiles = Directory.GetFiles(mountPoint, "*", SearchOption.AllDirectories).ToList();

            imageFiles
                .ForEach(realImageFile =>
                {
                    var partitionName = Path.GetFileNameWithoutExtension(realImageFile);
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
            AddPartitionImagesToVirtualFolder(partitionContainer.Partitions, root, "");

            Log.Information($"Mounting partition images to: {mountAsImageOptions.MountPoint}");

            var vfs = new DokanVFS(root, PROGRAM_NAME);
            vfs.Mount(mountAsImageOptions.MountPoint, DokanOptions.WriteProtection, new DokanNet.Logging.NullLogger());
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

            AddPartitionImagesToVirtualFolder(partitionContainer.Partitions, root, imagesRootFolder);

            var imagesRoot = root.CreateOrRetrieve(imagesRootFolder);
            imagesRoot.Hidden = true;

            var vfs = new DokanVFS(root, PROGRAM_NAME);
            Task.Factory.StartNew(() => vfs.Mount(mountAsFilesOptions.MountPoint, DokanOptions.WriteProtection, new DokanNet.Logging.NullLogger()));
            WaitForMountPointToBeAvailable(mountAsFilesOptions.MountPoint);

            Log.Information($"Retrieving a list of files.");

            //use 7z to get a list of files
            var realImagesFolder = Path.Combine(mountAsFilesOptions.MountPoint, imagesRootFolder);
            var imageFiles = Directory.GetFiles(realImagesFolder, "*", SearchOption.AllDirectories).ToList();

            imageFiles
                .ForEach(realImageFile =>
                {
                    var partitionName = Path.GetFileNameWithoutExtension(realImageFile);
                    var partitionCache = clonezillaCacheManager.GetPartitionCache(partitionName);

                    var archiveFiles = GetFilesInPartition(realImageFile, partitionCache);


                    //var extractedLookup = new Dictionary<string, string>();

                    foreach (var archiveEntry in archiveFiles)
                    {
                        //Log.Information($"Found file in archive: {archiveEntry.Path}");

                        FileSystemEntry vfsEntry;
                        if (archiveEntry.IsFolder)
                        {
                            var virtualFolderPath = archiveEntry.Path.Replace(@"\.", "");
                            virtualFolderPath = Path.Combine(partitionName, virtualFolderPath);

                            vfsEntry = root.CreateOrRetrieve(virtualFolderPath);
                        }
                        else
                        {
                            var virtualFilePath = Path.Combine(partitionName, archiveEntry.Path);

                            var virtualFileName = Path.GetFileName(virtualFilePath);
                            var virtualFolderPath = Path.GetDirectoryName(virtualFilePath) ?? throw new Exception($"Could not retrieve folder for: {virtualFilePath}");

                            var virtualFolder = root.CreateOrRetrieve(virtualFolderPath);

                            vfsEntry = new FileEntry(
                                virtualFileName,
                                () =>
                                {
                                    /*
                                    //Works, but not to think about how to clean up temp files reliably, particularly given that multiple instances of the program can be running
                                    if (!extractedLookup.ContainsKey(archiveEntry.Path))
                                    {
                                        var tempFilename = Path.GetRandomFileName();
                                        tempFilename = Path.Combine(clonezillaCacheManager.TempFolder, tempFilename);
                                        File.Create(tempFilename).Close();
                                        File.SetAttributes(tempFilename, FileAttributes.Temporary); //not sure if this is needed, given that we use DeleteOnClose in the next call. But according to its doco, it gives a hint to the OS to mainly keep it in memory.
                                        //var tempFileStream = new FileStream(tempFilename, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose); //DeleteOnClose doesn't play nicely with the subsequent read.
                                        var tempFileStream = new FileStream(tempFilename, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.ReadWrite);

                                        lock (vfs)
                                        {
                                            Log.Debug($"Extracting {archiveEntry.Path} from {realImageFile}");
                                            SevenZipUtility.ExtractFileFromArchive(realImageFile, archiveEntry.Path, tempFileStream);
                                            Log.Debug($"Finished extracting {archiveEntry.Path} from {realImageFile}");
                                        }

                                        extractedLookup.Add(archiveEntry.Path, tempFilename);
                                    }

                                    var tempFilenameStr = extractedLookup[archiveEntry.Path];
                                    var stream = File.Open(tempFilenameStr, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
                                    */


                                    var stream = new MemoryStream();
                                    //lock (vfs)
                                    {
                                        Log.Debug($"Extracting {archiveEntry.Path} from {realImageFile}");
                                        SevenZipUtility.ExtractFileFromArchive(realImageFile, archiveEntry.Path, stream);
                                        Log.Debug($"Finished extracting {archiveEntry.Path} from {realImageFile}");
                                    }
                                    stream.Seek(0, SeekOrigin.Begin);


                                    return stream;
                                })
                            {
                                Created = archiveEntry.Created,
                                Accessed = archiveEntry.Accessed,
                                Modified = archiveEntry.Modified,
                                Length = archiveEntry.Size
                            };

                            virtualFolder.Children.Add(vfsEntry);
                        }

                        vfsEntry.Created = archiveEntry.Created;
                        vfsEntry.Modified = archiveEntry.Modified;
                        vfsEntry.Accessed = archiveEntry.Accessed;
                    };
                });

            Log.Information($"Mounting complete. Mounted to: {mountAsFilesOptions.MountPoint}");
            Console.WriteLine("Running. Press any key to exit.");
            Console.ReadKey();
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

        static void AddPartitionImagesToVirtualFolder(List<Partition> partitions, Folder root, string virtualFolderPath)
        {
            var virtualFileEntries = partitions
                                        .Select(partition =>
                                        {
                                            var virtualImageFilename = Path.Combine(virtualFolderPath, $"{partition.Name}.img");

                                            var virtualFileName = Path.GetFileName(virtualImageFilename);

                                            var virtualFolder = root.CreateOrRetrieve(virtualFolderPath);

                                            var fileEntry = new FileEntry(virtualFileName, () => partition.FullPartitionImage)
                                            {
                                                Created = DateTime.Now,
                                                Accessed = DateTime.Now,
                                                Modified = DateTime.Now,
                                                Length = partition.FullPartitionImage.Length,
                                            };

                                            virtualFolder.Children.Add(fileEntry);

                                            return fileEntry;
                                        })
                                        .ToList();
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
