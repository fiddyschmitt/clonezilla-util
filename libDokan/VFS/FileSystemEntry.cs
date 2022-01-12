using DokanNet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace libDokan.VFS
{
    public abstract class FileSystemEntry
    {
        public string Name { get; set; }

        [IgnoreDataMember]
        public string? FullPath { get; set; }

        public bool Hidden { get; set; } = false;
        public bool System { get; set; } = false;

        public FileSystemEntry(string name)
        {
            Name = name;
        }

        public DateTime Modified;
        public DateTime Created;
        public DateTime Accessed;

        protected abstract FileInformation ToFileInfo();

        public FileInformation ToFileInformation()
        {
            var result = ToFileInfo();

            if (Hidden) result.Attributes |= FileAttributes.Hidden;
            if (System) result.Attributes |= FileAttributes.System;

            result.FileName = Name;
            result.CreationTime = Created;
            result.LastAccessTime = Accessed;
            result.LastWriteTime = Modified;

            return result;
        }

        public override string ToString()
        {
            var result = Name;
            return result;
        }
    }
}
