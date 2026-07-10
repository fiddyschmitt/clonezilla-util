using lib7Zip;
using libClonezilla.Cache.FileSystem;
using libClonezilla.Partitions;
using libCommon.Streams;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using System;
using System.Collections.Generic;
using System.Text;

namespace libClonezilla.Cache
{
    public interface IPartitionCache
    {
        public string GetGztoolIndexFilename();
        public string GetBZip2IndexFilename();
        public string GetZstdIndexFilename();
        public List<ArchiveEntry>? GetFileList();
        public void SetFileList(List<ArchiveEntry> filenames);
    }
}
