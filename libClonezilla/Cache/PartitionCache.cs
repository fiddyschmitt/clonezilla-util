using libClonezilla.Cache.FileSystem;
using System.Linq;
using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using libPartclone;
using libGZip;
using libCommon.Streams;
using System.Collections.ObjectModel;
using libPartclone.Cache;
using lib7Zip;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;

namespace libClonezilla.Cache
{
    public class PartitionCache : IPartcloneCache, IPartitionCache
    {
        public string ClonezillaCacheFolder { get; }
        public string PartitionName { get; }

        private readonly string PartcloneContentMappingFilename;
        private readonly string FileListFilename;

        public string VFSEntriesFilename { get; }

        public PartitionCache(string clonezillaCacheFolder, string partitionName)
        {
            ClonezillaCacheFolder = clonezillaCacheFolder;
            PartitionName = partitionName;

            PartcloneContentMappingFilename = Path.Combine(ClonezillaCacheFolder, $"{partitionName}.PartcloneContentMapping.json");
            FileListFilename = Path.Combine(ClonezillaCacheFolder, $"{partitionName}.Files.json");
            VFSEntriesFilename = Path.Combine(ClonezillaCacheFolder, $"{partitionName}.VFS.Files.json");
        }

        public string GetGztoolIndexFilename()
        {
            string result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.gztool_index.gzi");
            return result;
        }

        public List<ContiguousRange>? GetPartcloneContentMapping()
        {
            List<ContiguousRange>? result = null;

            if (File.Exists(PartcloneContentMappingFilename))
            {
                string json = File.ReadAllText(PartcloneContentMappingFilename);
                result = JsonConvert.DeserializeObject<List<ContiguousRange>>(json);
            }

            return result;
        }

        public void SetPartcloneContentMapping(List<ContiguousRange> contiguousRanges)
        {
            var json = JsonConvert.SerializeObject(contiguousRanges, Formatting.Indented);
            File.WriteAllText(PartcloneContentMappingFilename, json);
        }

        public List<ArchiveEntry>? GetFileList()
        {
            List<ArchiveEntry>? result = null;

            if (File.Exists(FileListFilename))
            {
                string json = File.ReadAllText(FileListFilename);
                result = JsonConvert.DeserializeObject<List<ArchiveEntry>>(json);
            }

            return result;
        }

        public void SetFileList(List<ArchiveEntry> filenames)
        {
            var json = JsonConvert.SerializeObject(filenames, Formatting.Indented);
            File.WriteAllText(FileListFilename, json);
        }

        public Folder? GetVFSFolder()
        {
            Folder? result = null;

            if (File.Exists(VFSEntriesFilename))
            {
                string json = File.ReadAllText(VFSEntriesFilename);

                var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto, Formatting = Formatting.Indented };

                result = JsonConvert.DeserializeObject<Folder>(json, settings);
            }

            return result;
        }

        public void SetVFSFolder(Folder entries)
        {
            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto, Formatting = Formatting.Indented };

            var json = JsonConvert.SerializeObject(entries, typeof(Folder), settings);

            File.WriteAllText(VFSEntriesFilename, json);
        }
    }
}
