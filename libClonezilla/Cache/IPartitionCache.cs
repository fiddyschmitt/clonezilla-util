using lib7Zip;
using libClonezilla.Cache.FileSystem;
using libClonezilla.Partitions;
using libCommon.Streams;
using libPartclone;
using libPartclone.Cache;
using System;
using System.Collections.Generic;
using System.Text;

namespace libClonezilla.Cache
{
    public interface IPartitionCache
    {
        public string GetGztoolIndexFilename();
        public List<ArchiveEntry>? GetFileList();
        public void SetFileList(List<ArchiveEntry> filenames);
    }
}
