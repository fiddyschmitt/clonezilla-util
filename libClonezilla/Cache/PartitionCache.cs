using libClonezilla.Cache.FileSystem;
using System.Linq;
using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using libCommon.Streams;
using System.Collections.ObjectModel;
using lib7Zip;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using Serilog;

namespace libClonezilla.Cache
{
    public class PartitionCache : IPartitionCache
    {
        public string ClonezillaCacheFolder { get; }
        public string PartitionName { get; }

        private readonly string FileListFilename;

        public PartitionCache(string clonezillaCacheFolder, string partitionName)
        {
            ClonezillaCacheFolder = clonezillaCacheFolder;
            PartitionName = partitionName;

            FileListFilename = Path.Combine(ClonezillaCacheFolder, $"{partitionName}.Files.json");
        }

        public string GetGztoolIndexFilename()
        {
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.gztool_index.gzi");
            return result;
        }

        public string GetBZip2IndexFilename()
        {
            //v2: bit-aligned block boundaries (Batch 8b). The old byte-aligned ".bzip2_index.json"
            //still decodes correctly if present, but is coarse (merged blocks that can only serve
            //single-threaded); the new name forces a one-time rebuild at the finer granularity.
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.bzip2_index_v2.json");
            return result;
        }

        public string GetZstdIndexFilename()
        {
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.zstd_index.zsi");
            return result;
        }

        public string GetXzIndexFilename()
        {
            //single-block xz only: LZMA2-chunk checkpoint index (multi-block xz uses the file's
            //native footer index and needs no cache file).
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.xz_index.xzi");
            return result;
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
            try
            {
                using var fs = File.Create(FileListFilename);
                using var sw = new StreamWriter(fs);
                using var writer = new JsonTextWriter(sw);

                writer.Formatting = Formatting.Indented;

                var serializer = JsonSerializer.CreateDefault();
                serializer.Serialize(writer, filenames);
            }
            catch (Exception ex)
            {
                Log.Warning($"Non-fatal. Error while caching File List to {FileListFilename}: {ex}");
            }
        }
    }
}
