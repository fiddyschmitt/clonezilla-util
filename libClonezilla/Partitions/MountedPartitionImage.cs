using clonezilla_util.VFS;
using lib7Zip;
using lib7Zip.Native;
using libClonezilla.Cache;
using libClonezilla.Extractors;
using libClonezilla.PartitionContainers;
using libClonezilla.VFS;
using libCommon;
using libCommon.Streams;
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
        public MountedPartitionImage(Partition partition, Folder partitionFolder, Lazy<IVFS> vfs, DesiredContent desiredContent)
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
                imagesFolder = vfs.Value.CreateTempFolder();
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

            // Feed the native engine the partition's in-process decompressed stream directly (NOT the
            // Dokan path) - reading our own mount in-process risks a Dokan callback-thread deadlock. Each
            // native 7z worker gets its own independent cursor over it (lock-free on the compressed serving
            // path, so the workers decode in parallel - see Partition.CreateReaderFactory).
            var partitionStreamFactory = Partition.CreateReaderFactory();

            IExtractor extractor;
            List<ArchiveEntry> filesInArchive;

            try
            {
                // Building the extractor opens (and pre-warms) the partition - one unreadable partition
                // must not bring down the whole mount, so this is inside the try.
                extractor = DetermineExtractor.FindExtractor(partitionStreamFactory);

                if (extractor is IFileListProvider fileListProvider)
                {
                    filesInArchive = Partition.GetFilesInPartition(fileListProvider).ToList();

                    //S4: no Stream.Synchronized wrapper here. Every consumer of these streams already
                    //serialises: DokanVFS.ReadFile locks the per-handle stream's ReadLock (each handle
                    //owns its own stream - CreatesNewStreamPerCall), ReadForMemoryMap keeps a dedicated
                    //stream under its own lock, and ReleaseContext disposes under the same handle lock.
                    //The wrapper only added a lock acquisition to every read (L12 lock inventory, #2).
                }
                else
                {
                    Log.Error($"[{container.ContainerName}] [{partitionName}] Could not find a suitable extractor for this partition. Returning empty file list.");
                    filesInArchive = [];
                }
            }
            catch (NotAnArchiveException)
            {
                //expected: this partition has no filesystem 7-Zip can browse (e.g. a raw bios_grub partition). Serve it as empty.
                Log.Information($"[{container.ContainerName}] [{partitionName}] No browsable filesystem found in this partition. Serving it with no files.");
                return [];
            }
            catch (DllNotFoundException ex)
            {
                //The native 7-Zip engine (lib7zNative.dll) failed to LOAD. That is global, not a property
                //of this partition - EVERY partition would be empty - so surface it clearly and fatally
                //rather than silently serving an empty drive after a long index build (gh #89). The
                //historical cause was lib7zNative depending on the VC++ runtime (VCRUNTIME140.dll); the
                //shipped DLL is now statically linked (build.ps1 /MT), so this guards future regressions.
                Log.Fatal(ex, $"The native 7-Zip engine (lib7zNative.dll) could not be loaded, so file "
                            + $"contents cannot be read. This is not specific to [{partitionName}] - it affects the whole mount. "
                            + $"If you built clonezilla-util yourself, build lib7zNative with the static CRT (/MT); "
                            + $"otherwise please report this at https://github.com/fiddyschmitt/clonezilla-util/issues with your Windows version.");
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[{container.ContainerName}] [{partitionName}] Error while getting files in partition. Returning empty file list.");
                return [];
            }

            Log.Information($"[{container.ContainerName}] [{partitionName}] Contains {filesInArchive.Count:N0} files.");

            Log.Information($"[{container.ContainerName}] [{partitionName}] Creating file system tree.");
            var tree = CreateTree(container.ContainerName, partitionName, filesInArchive, extractor);

            return tree;
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
                                            //trim only the trailing "\." - a blanket Replace would corrupt dot-folders elsewhere in the path (eg. home\.config\.)
                                            virtualFolderPath = virtualFolderPath[..^2];
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

            //A folder the listing never mentions (a handler may list only files) is synthesised, with any missing
            //ancestors, so its children keep their place. They used to be promoted to the partition root under their
            //leaf names (gh review F27).
            var synthesisedFolders = new List<Folder>();
            Folder? GetOrCreateFolder(string folderPath)
            {
                if (folderLookup.TryGetValue(folderPath, out var existing)) return existing;

                var folder = new Folder(Path.GetFileName(folderPath), null);
                folderLookup[folderPath] = folder;
                synthesisedFolders.Add(folder);
                folder.Parent = GetOrCreateFolder(Path.GetDirectoryName(folderPath) ?? "");
                return folder;
            }

            Log.Debug($"[{containerName}] [{partitionName}] Establishing parent/child relationships.");
            entries
                .Select(entry => entry.NewEntry)
                .Cast<FileSystemEntry>()
                .ForEach(fileSystemEntry =>
                {
                    fileSystemEntry.Parent = GetOrCreateFolder(pathLookup[fileSystemEntry]);
                });

            if (synthesisedFolders.Count > 0)
            {
                Log.Information($"[{containerName}] [{partitionName}] {synthesisedFolders.Count:N0} folder(s) were missing from the listing; created them so their contents keep their paths.");
            }

            var tree = entries
                            .Select(entry => entry.NewEntry)
                            .Cast<FileSystemEntry>()
                            .Concat(synthesisedFolders)
                            .Where(entry => entry.Parent == null)
                            .ToList();

            return tree;
        }
    }
}
