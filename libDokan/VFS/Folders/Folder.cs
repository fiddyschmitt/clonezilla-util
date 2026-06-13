using DokanNet;
using libDokan.VFS.Files;
using System;
using System.Collections.Generic;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.VFS.Folders
{
    public class Folder : FileSystemEntry
    {
        readonly List<FileSystemEntry> children = [];
        readonly object childrenLock = new();

        //returns a snapshot, because Dokan callbacks can enumerate while other threads are still populating
        public IEnumerable<FileSystemEntry> Children
        {
            get
            {
                lock (childrenLock)
                {
                    return children.ToList();
                }
            }
        }

        public void AddChild(FileSystemEntry entry)
        {
            lock (childrenLock)
            {
                if (!children.Contains(entry))
                {
                    children.Add(entry);
                    entry.Parent = this;
                }
            }
        }

        public void AddChildren(IEnumerable<FileSystemEntry> entries)
        {
            entries
                .ToList()
                .ForEach(entry => AddChild(entry));
        }

        public Folder(string name, Folder? parent) : base(name, parent)
        {
        }

        protected override FileInformation ToFileInfo()
        {
            var result = new FileInformation();

            result.Attributes |= FileAttributes.Directory;

            return result;
        }

        public override string ToString()
        {
            var result = $"{Name}";
            return result;
        }
    }
}
