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

        public string GetGzipIndexFilename()
        {
            //GzipSeekable zran checkpoint index. (The old gztool-era "{name}.gztool_index.gzi"
            //files are dead weight since gztool was decommissioned - a cache clear removes them.)
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.gzip_index.gzsi");
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



        //System.Text.Json over the raw UTF-8 stream: profiling (TEST_ANALYSIS.md #2) showed the
        //old Newtonsoft + ReadAllText path spending ~25 s per 721k-file partition on every open
        //(a 278 MB file materialised as a 556 MB string, then per-entry DateTime parsing).
        //The JSON shape is unchanged, so caches written by the old code still load; new files are
        //written compact (no indentation), which also shrinks them substantially.
        static readonly System.Text.Json.JsonSerializerOptions FileListJsonOptions = new()
        {
            IncludeFields = true,
        };

        public string GetServingDecisionFilename()
        {
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.serving_decision.txt");
            return result;
        }

        public List<ArchiveEntry>? GetFileList()
        {
            List<ArchiveEntry>? result = null;

            if (File.Exists(FileListFilename))
            {
                using var fs = File.OpenRead(FileListFilename);
                result = System.Text.Json.JsonSerializer.Deserialize<List<ArchiveEntry>>(fs, FileListJsonOptions);
            }

            return result;
        }

        public void SetFileList(List<ArchiveEntry> filenames)
        {
            try
            {
                using var fs = File.Create(FileListFilename);
                System.Text.Json.JsonSerializer.Serialize(fs, filenames, FileListJsonOptions);
            }
            catch (Exception ex)
            {
                Log.Warning($"Non-fatal. Error while caching File List to {FileListFilename}: {ex}");
            }
        }
    }
}
