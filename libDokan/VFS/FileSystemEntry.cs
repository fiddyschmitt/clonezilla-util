using DokanNet;
using libCommon;
using libDokan.VFS.Folders;
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

        Folder? parent;
        public Folder? Parent
        {
            get
            {
                return parent;
            }
            set
            {
                parent = value;
                parent?.AddChild(this);
            }
        }
        public bool Hidden { get; set; } = false;
        public bool System { get; set; } = false;

        public FileSystemEntry(string name, Folder? parent)
        {
            Name = name;
            Parent = parent;
        }

        public DateTime Modified;
        public DateTime Created;
        public DateTime Accessed;

        protected abstract FileInformation ToFileInfo();

        public string FullPath
        {
            get
            {
                var ancestors = this
                                    .Recurse(ancestor => ancestor.Parent)
                                    .Reverse()
                                    .ToList();

                var result = ancestors
                                .Where(a => !string.IsNullOrEmpty(a.Name))
                                .Select(a => a.Name)
                                .ToString("\\");

                return result;
            }
        }

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
