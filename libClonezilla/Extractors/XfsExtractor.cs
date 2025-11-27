using DiscUtils;
using DiscUtils.Partitions;
using DiscUtils.Raw;
using DiscUtils.Streams;
using DiscUtils.Xfs;
using DokanNet.Logging;
using lib7Zip;
using libClonezilla.PartitionContainers.ImageFiles;
using libCommon;
using libCommon.Streams;
using libDokan.VFS.Folders;
using LTRData;
using Newtonsoft.Json.Linq;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class XfsExtractor : IExtractor, IFileListProvider
    {
        protected XfsFileSystem xfsStream;

        public XfsExtractor(string path)
        {
            //var types = FileSystemManager.DetectFileSystems(fileStream);


            //using var disk = new Disk(fileStream, Ownership.None);
            //var manager = new VolumeManager(disk);
            //var logicalVolumes = manager.GetLogicalVolumes();

            //var volume = logicalVolumes.First();

            //using Stream volumeStream = volume.Open();
            //try
            //{
            //    using var xfs = new XfsFileSystem(volumeStream);

            //    foreach (var file in xfs.GetFiles(xfs.Root.FullName, "*.*", SearchOption.AllDirectories))
            //    {
            //        Console.WriteLine(file);
            //    }
            //}
            //catch (IOException ex)
            //{
            //    Console.WriteLine("Could not initialize XFS system.");
            //}

            var fs = File.OpenRead(path);
            xfsStream = new XfsFileSystem(fs);
        }

        public Stream Extract(string path)
        {
            var result = xfsStream.OpenFile(path, FileMode.Open, FileAccess.Read);

            return result;
        }

        public IEnumerable<ArchiveEntry> GetFileList()
        {
            var allFolders = new List<string>() { "" }
                                .Recurse(folder =>
                                {
                                    var subfolders = xfsStream
                                                        .GetDirectories(folder)
                                                        .ToList();

                                    return subfolders;
                                })
                                .ToList();

            var entries = allFolders
                            .SelectMany(folder =>
                            {
                                var entries = xfsStream
                                                .GetFileSystemEntries(folder)
                                                .Select(entry =>
                                                {
                                                    var entryInfo = xfsStream.GetFileSystemInfo(entry);

                                                    var archiveEntry = new ArchiveEntry(entryInfo.FullName)
                                                    {
                                                        Created = entryInfo.CreationTime,
                                                        Accessed = entryInfo.LastAccessTime,
                                                        Modified = entryInfo.LastWriteTime,

                                                        IsFolder = entryInfo.Attributes.HasFlag(FileAttributes.Directory),
                                                        Offset = 0
                                                    };

                                                    if (!archiveEntry.IsFolder)
                                                    {
                                                        var fileEntry = xfsStream.GetFileInfo(entry);
                                                        archiveEntry.Size = fileEntry.Length;
                                                    }

                                                    archiveEntry.Path = Path.TrimEndingDirectorySeparator(archiveEntry.Path);

                                                    return archiveEntry;
                                                });

                                return entries;
                            })
                            .ToList();

            return entries;
        }
    }
}
