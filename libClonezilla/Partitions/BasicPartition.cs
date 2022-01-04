using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
using libPartclone;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace libClonezilla.Partitions
{
    public class BasicPartition : Partition
    {
        public BasicPartition(string name, PartcloneStream fullPartitionImage) : base(name, fullPartitionImage)
        {
        }

        public override Stream? GetFile(string filename)
        {
            return null;
        }

        public override IEnumerable<FileDetails> GetFilesInPartition()
        {
            return new List<FileDetails>();
        }

        public override IEnumerable<FolderDetails> GetFoldersInPartition()
        {
            return new List<FolderDetails>();
        }
    }
}
