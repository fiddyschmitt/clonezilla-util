using clonezilla_util.VFS;
using lib7Zip;
using libClonezilla.Cache;
using libClonezilla.Extractors;
using libClonezilla.PartitionContainers;
using libClonezilla.VFS;
using libCommon;
using libDokan.VFS;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace libClonezilla.Partitions
{
    public class MountedPartitionImage
    {
        public MountedPartitionImage(Partition partition, Folder partitionFolder, IVFS vfs, DesiredContent desiredContent)
        {
            Partition = partition;
            var imageFilename = $"{partition.PartitionName}.img";

            if (partition.FullPartitionImage == null) throw new Exception($"Cannot mount partition. {nameof(partition.FullPartitionImage)} has not been intialised.");

            Folder imagesFolder;
            if (desiredContent == DesiredContent.ImageFiles)
            {
                imagesFolder = partitionFolder;
            }
            else
            {
                imagesFolder = vfs.CreateTempFolder();
            }

            ImageFileEntry = new StreamBackedFileEntry(imageFilename, imagesFolder, partition.FullPartitionImage)
            {
                Created = DateTime.Now,
                Accessed = DateTime.Now,
                Modified = DateTime.Now
            };

            if (desiredContent == DesiredContent.Files)
            {
                var tree = GetTree();
                partitionFolder.AddChildren(tree);
            }
        }

        public enum DesiredContent
        {
            ImageFiles,
            Files
        }

        public Partition Partition { get; }

        public FileEntry ImageFileEntry { get; }

        public List<FileSystemEntry> GetTree()
        {
            var container = Partition.Container;
            var partitionName = Partition.PartitionName;

            Log.Information($"[{container.ContainerName}] [{partitionName}] Retrieving a list of files.");

            List<ArchiveEntry> filesInArchive;

            try
            {
                filesInArchive = GetFilesInPartition().ToList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[{container.ContainerName}] [{partitionName}] The image file for this partition ({ImageFileEntry.FullPath}) is not considered an archive by 7-Zip. Returning empty file list.");
                return [];
            }

            Log.Information($"[{container.ContainerName}] [{partitionName}] Contains {filesInArchive.Count:N0} files.");

            //Do a performance test. If the archive can be opened quickly, then use 7z.exe which is slow but reliable. If it takes a long time, then use 7zFM which is fast but less reliable.
            var performanceTestTimeout = TimeSpan.FromSeconds(10);
            var performanceTestCancellationToken = new CancellationTokenSource();

            var performanceTestTask = Task.Factory.StartNew(() =>
            {
                Log.Information($"[{container.ContainerName}] [{partitionName}] Determining optimal way to extract files from this partition.");
                var testStart = DateTime.Now;

                SevenZipUtility.IsArchive(ImageFileEntry.FullPath, performanceTestCancellationToken);

                if (performanceTestCancellationToken.IsCancellationRequested)
                {
                    Log.Debug($"[{container.ContainerName}] [{partitionName}] Test did not finish within the {performanceTestTimeout.TotalSeconds:N0)} second timeout.");
                }
                else
                {
                    var testDuration = DateTime.Now - testStart;
                    Log.Debug($"[{container.ContainerName}] [{partitionName}] Archive opened in {testDuration.TotalSeconds:N1} seconds.");
                }
            });

            bool use7z;
            if (Task.WhenAny(performanceTestTask, Task.Delay(performanceTestTimeout)).Result == performanceTestTask)
            {
                // task completed within timeout
                use7z = true;
            }
            else
            {
                // timed out. Cancel the test
                performanceTestCancellationToken.Cancel();
                performanceTestTask.Wait();

                use7z = false;
            }

            IExtractor extractor;
            if (use7z)
            {
                Log.Information($"[{container.ContainerName}] [{partitionName}] 7z.exe will be used to extract files from this partition.");

                //Extractor which uses 7z.exe.
                //It runs the process and returns its stdout straight away, so it's non-blocking.
                //Works fine, except it's too slow when extracting from a large archive, causing explorer.exe to assume the file isn't available.
                extractor = new ExtractorUsing7z(ImageFileEntry.FullPath);

                //This actually causes errors for FLP, because FLP uses more threads than are being served
                /*
                var extractors = new List<IExtractor>();
                for (int i = 0; i < 8; i++)
                {
                    var newExtractor = new ExtractorUsing7z(ImageFileEntry.FullPath);
                    extractors.Add(newExtractor);
                }

                extractor = new MultiExtractor(extractors, true);
                */
            }
            else
            {
                Log.Information($"[{container.ContainerName}] [{partitionName}] 7zFM.exe will be used to extract files from this partition.");

                var instanceCount = 4;
                Log.Information($"[{container.ContainerName}] [{partitionName}] Starting {instanceCount:N0} instances of 7zFM.exe.");

                //Extractor which uses the 7-Zip File Manager
                //Opens the archive here, up front. Subsequent extracts are quick.
                //Creating them in parallel is ideal, because the data each needs is available in the memory cache.
                var extractors = Enumerable
                                    .Range(1, instanceCount)
                                    .AsParallel()
                                    .WithDegreeOfParallelism(4)
                                    .Select(_ => new ExtractorUsing7zFM(ImageFileEntry.FullPath))
                                    .OfType<IExtractor>()
                                    .ToList();

                extractor = new MultiExtractor(extractors, true);
            }




            /*
            //Was hoping that these libraries could load the archive (which takes time) in the constructor, so that it would be more responsive when asked to extract a file from it.
            var partitionDetails = container
                                    .Partitions
                                    .FirstOrDefault(partition => partition.PartitionName.Equals(partitionName)) ?? throw new Exception($"Could not load details for {partitionName}.");

            var extractors = new List<IExtractor>();
            for (int i = 0; i < 4; i++)
            {
                //Uses Squid-Box.SevenZipSharp. Throws an exception when trying to extract files:
                //Execution has failed due to an internal SevenZipSharp issue (0x800705aa / -2147023446). You might find more info at https://github.com/squid-box/SevenZipSharp/issues/, but this library is no longer actively supported
                //var newExtractor = new ExtractorUsingSevenZipSharp(ImageFileEntry.FullPath);

                //Uses SevenZipExtractor, but throws SevenZipExtractor.SevenZipException: 'partition0.img is not a known archive type'
                //var newExtractor = new ExtractorUsingSevenZipExtractor(ImageFileEntry.FullPath);

                //Uses SevenZipExtractor, but throws SevenZipExtractor.SevenZipException: 'Unable to guess format automatically'
                var newExtractor = new ExtractorUsingSevenZipExtractor(File.OpenRead(ImageFileEntry.FullPath));

                extractors.Add(newExtractor);
            }

            extractor = new MultiExtractor(extractors, true);
            */




            //this layer caches streams as they're created, so that they are extracted only once
            extractor = new CachedExtractor(extractor);

            //this layer makes the extractor thread-safe by wrapping each stream in a Stream.Synchronized()
            extractor = new SynchronisedExtractor(extractor);


            var tree = CreateTree(container.ContainerName, partitionName, filesInArchive, extractor);

            return tree;
        }

        public IEnumerable<ArchiveEntry> GetFilesInPartition()
        {
            var partitionCache = Partition.PartitionCache;
            IEnumerable<ArchiveEntry>? archiveFiles = partitionCache?.GetFileList();

            bool saveToCache = false;
            if (archiveFiles == null)
            {
                archiveFiles = SevenZipUtility.GetArchiveEntries(ImageFileEntry.FullPath, false, true);
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

        protected static List<FileSystemEntry> CreateTree(string containerName, string partitionName, List<ArchiveEntry> allEntries, IExtractor extractor)
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
                                .GroupBy(
                                    entry => entry.PathInArchive,
                                    entry => entry,
                                    (key, grp) => grp.First()
                                )
                                .ToList();

            var folderLookup = entries
                                .Where(entry => entry.NewEntry is Folder)
                                .ToDictionary(
                                    entry => entry.PathInArchive,
                                    entry => entry.NewEntry as Folder);

            var pathLookup = entries
                                .ToDictionary(
                                    vfsEntry => vfsEntry.NewEntry,
                                    vfsEntry => vfsEntry.ContainingFolder);

            folderLookup.Add("", null);

            Log.Debug($"[{containerName}] [{partitionName}] Establishing parent/child relationships.");
            entries
                .Select(entry => entry.NewEntry)
                .Cast<FileSystemEntry>()
                .ForEach(fileSystemEntry =>
                {
                    var parentFolder = pathLookup[fileSystemEntry];

                    if (folderLookup.TryGetValue(parentFolder, out Folder? value))
                    {
                        fileSystemEntry.Parent = value;
                    }
                    else
                    {
                        Log.Error($"Error while finding parent of {fileSystemEntry}");
                        Log.Error($"Could not find {parentFolder} in {nameof(folderLookup)}");
                    }
                });

            var tree = entries
                            .Select(entry => entry.NewEntry)
                            .Cast<FileSystemEntry>()
                            .Where(entry => entry.Parent == null)
                            .ToList();

            return tree;
        }
    }
}
