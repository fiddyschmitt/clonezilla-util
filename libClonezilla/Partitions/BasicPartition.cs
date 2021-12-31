using libClonezilla.Cache;
using libClonezilla.Cache.FileSystem;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace libClonezilla.Partitions
{
    public class BasicPartition : Partition
    {
        public BasicPartition(string name, string type, Stream fullPartitionImage) : base(name, type, fullPartitionImage)
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
