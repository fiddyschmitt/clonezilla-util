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
        //reference-identity membership: O(1) de-dup, and it terminates the re-entrant Parent-setter call below
        readonly HashSet<FileSystemEntry> childrenSet = [];
        //name -> first-inserted child of that name: O(1) case-insensitive lookup (mirrors the old FirstOrDefault-by-insertion-order scan)
        readonly Dictionary<string, FileSystemEntry> childrenByName = new(StringComparer.OrdinalIgnoreCase);
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
                //HashSet.Add returns false if the entry is already present. This both de-dups and stops the
                //recursion caused by the Parent setter (below) calling AddChild again.
                if (!childrenSet.Add(entry))
                {
                    return;
                }

                children.Add(entry);                        //the list keeps every child, in insertion order (preserves enumeration, including case-distinct siblings)
                childrenByName.TryAdd(entry.Name, entry);   //the dictionary keeps the first child of each name (matches the old FirstOrDefault-by-insertion-order)
                entry.Parent = this;
            }
        }

        //O(1) case-insensitive lookup of a child by name. Returns the first-inserted entry with that name, or null.
        public FileSystemEntry? GetChild(string name)
        {
            lock (childrenLock)
            {
                childrenByName.TryGetValue(name, out var entry);
                return entry;
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
