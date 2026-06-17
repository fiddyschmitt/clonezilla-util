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
                //walk the parent chain (this -> ... -> root), then reverse to root-first.
                //Equivalent to Recurse(a => a.Parent).Reverse().ToList() but without the
                //iterator/closure overhead; Ancestors is read on Dokan callback paths.
                var ancestors = new List<FileSystemEntry>();
                for (FileSystemEntry? node = this; node != null; node = node.Parent)
                {
                    ancestors.Add(node);
                }
                ancestors.Reverse();

                return ancestors;
            }
        }

        public bool IsAccessibleToProcess(int requestPID)
        {
            //check if any of the ancestors are restricted. Walk the parent chain directly and
            //only build a ProcInfo if there's actually a restricted ancestor to test against
            //(this runs per FindFiles call; the common case has no restricted ancestors at all).
            ProcInfo? procInfo = null;
            for (FileSystemEntry? node = this; node != null; node = node.Parent)
            {
                if (node is RestrictedFolderByPID restricted)
                {
                    procInfo ??= new ProcInfo(requestPID);
                    if (!restricted.IsProcessPermitted(procInfo))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public string FullPath
        {
            get
            {
                //compute Ancestors once (it allocates a list) instead of walking the chain twice
                var ancestors = Ancestors;

                var folderPath = ancestors
                                .Where(a => a is not RootFolder)
                                .Select(a => a.Name)
                                .ToString("\\");

                var root = ancestors
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
