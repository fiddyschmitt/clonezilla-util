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

            var filesInArchive = GetFilesInPartition().ToList();

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
                Log.Debug($"[{container.ContainerName}] [{partitionName}] Running a performance test to determine the optimal way to extract files from this image.");

                var testStart = DateTime.Now;
                var testExtractor = new ExtractorUsing7z(ImageFileEntry.FullPath);
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
                extractor = new ExtractorUsing7z(ImageFileEntry.FullPath);

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
                    var newExtractor = new ExtractorUsing7zFM(ImageFileEntry.FullPath);
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

                    var parent = folderLookup[parentFolder];

                    //if (parent == null) throw new Exception($"Parent not found for: {fileSystemEntry.FullPath}");

                    fileSystemEntry.Parent = parent;
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
