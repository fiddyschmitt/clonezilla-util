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
        public IEnumerable<FileSystemEntry> Children => children;

        public void AddChild(FileSystemEntry entry)
        {
            if (!children.Contains(entry))
            {
                children.Add(entry);
                entry.Parent = this;
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
