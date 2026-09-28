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
            //v2: denser checkpoints (TargetSpanBytes lowered from 32 MB, see xzDecompressor) so a random
            //read decodes far less than a full 32 MB span. The old ".xz_index.xzi" still decodes
            //correctly if present, but is coarse; the new name forces a one-time rebuild at the finer
            //granularity (a larger index, deliberately traded for much faster scattered xz reads).
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.xz_index_v2.xzi");
            return result;
        }

        public string GetUncompressedLengthFilename()
        {
            var result = Path.Combine(ClonezillaCacheFolder, $"{PartitionName}.uncompressed_length.txt");
            return result;
        }

        //System.Text.Json over the raw UTF-8 stream: profiling (TEST_ANALYSIS.md #2) showed the
        //old Newtonsoft + ReadAllText path spending ~25 s per 721k-file partition on every open
        //(a 278 MB file materialised as a 556 MB string, then per-entry DateTime parsing).
        //Serialization goes through the source-generated ArchiveEntryJsonContext - reflection-based
        //JsonSerializer is stripped by the trimmed publish (the exe crashed on type load with
        //FileNotFoundException for System.Text.Json). The JSON shape is unchanged, so caches
        //written by the old code still load; new files are written compact (no indentation).
        public List<ArchiveEntry>? GetFileList()
        {
            if (!File.Exists(FileListFilename))
            {
                return null;
            }

            try
            {
                using var fs = File.OpenRead(FileListFilename);
                return System.Text.Json.JsonSerializer.Deserialize(fs, ArchiveEntryJsonContext.Default.ListArchiveEntry);
            }
            catch (Exception ex)
            {
                //A truncated or otherwise unreadable cache (a kill mid-write before this was atomic, a
                //disk-full, a partial file) must NOT leave the partition mounting empty on every future
                //run. Discard it and return null so the caller re-lists and rewrites it (gh review F3).
                Log.Warning($"File list cache '{Path.GetFileName(FileListFilename)}' could not be read ({ex.Message}); it will be rebuilt.");
                try { File.Delete(FileListFilename); } catch { }
                return null;
            }
        }

        public void SetFileList(List<ArchiveEntry> filenames)
        {
            //Write to a temp file and atomically move it into place. A reader then only ever sees the old
            //complete file or the new complete file - never a half-written one - so a crash during the
            //(multi-second, for a large partition) write can't corrupt the cache (gh review F3).
            var tempFilename = FileListFilename + ".tmp";
            try
            {
                using (var fs = File.Create(tempFilename))
                {
                    System.Text.Json.JsonSerializer.Serialize(fs, filenames, ArchiveEntryJsonContext.Default.ListArchiveEntry);
                }

                File.Move(tempFilename, FileListFilename, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning($"Non-fatal. Error while caching File List to {FileListFilename}: {ex}");
                try { File.Delete(tempFilename); } catch { }
            }
        }
    }
}
