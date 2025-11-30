using DokanNet;
using libCommon;
using libDokan.Processes;
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

        public List<FileSystemEntry> Ancestors
        {
            get
            {
                var ancestors = this
                                    .Recurse(ancestor => ancestor.Parent)
                                    .Reverse()
                                    .ToList();

                return ancestors;
            }
        }

        public bool IsAccessibleToProcess(int requestPID)
        {
            //check if any of the ancestors are restricted

            var restrictedAncestors = Ancestors
                                        .OfType<RestrictedFolderByPID>()
                                        .ToList();

            var procInfo = new ProcInfo(requestPID);

            bool isRestricted = restrictedAncestors
                                    .Any(ancestor => !ancestor.IsProcessPermitted(procInfo));

            return !isRestricted;
        }

        public string FullPath
        {
            get
            {
                var folderPath = Ancestors
                                .Where(a => a is not RootFolder)
                                .Select(a => a.Name)
                                .ToString("\\");

                var root = Ancestors
                            .OfType<RootFolder>()
                            .FirstOrDefault()?.MountPoint ?? "";

                var fullPath = Path.Combine(root, folderPath);

                return fullPath;
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
