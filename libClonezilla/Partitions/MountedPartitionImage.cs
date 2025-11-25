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

            IExtractor extractor = new DetermineExtractor();
            var foundExtractor = extractor.Initialise(ImageFileEntry.FullPath);

            List<ArchiveEntry> filesInArchive;

            try
            {
                if (foundExtractor && extractor is IFileListProvider fileListProvider)
                {
                    filesInArchive = GetFilesInPartition(fileListProvider).ToList();

                    //this layer caches streams as they're created, so that they are extracted only once
                    extractor = new CachedExtractor(extractor);

                    //this layer makes the extractor thread-safe by wrapping each stream in a Stream.Synchronized()
                    extractor = new SynchronisedExtractor(extractor);
                }
                else
                {
                    Log.Error($"[{container.ContainerName}] [{partitionName}] Could not find a suitable extractor for this partition. Returning empty file list.");
                    filesInArchive = [];
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[{container.ContainerName}] [{partitionName}] Error while getting files in partition. Returning empty file list.");
                return [];
            }

            Log.Information($"[{container.ContainerName}] [{partitionName}] Contains {filesInArchive.Count:N0} files.");


            var tree = CreateTree(container.ContainerName, partitionName, filesInArchive, extractor);

            return tree;
        }

        public IEnumerable<ArchiveEntry> GetFilesInPartition(IFileListProvider fileListProvider)
        {
            var partitionCache = Partition.PartitionCache;
            IEnumerable<ArchiveEntry>? archiveFiles = partitionCache?.GetFileList();

            bool saveToCache = false;
            if (archiveFiles == null)
            {
                archiveFiles = fileListProvider.GetFileList();
                saveToCache = true;
            }

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
