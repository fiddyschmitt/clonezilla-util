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
        List<FileSystemEntry> children = new List<FileSystemEntry>();
        public IEnumerable<FileSystemEntry> Children => children;

        public Folder CreateOrRetrieveFolder(string folderPath)
        {
            Folder result;

            var entry = GetEntryFromPath(folderPath, true);

            if (entry == null) throw new Exception($"Could not create folder: {folderPath}");
            if (entry is not Folder folder) throw new Exception($"Retrieved entry is not a folder for path: {folderPath}");

            result = folder;

            return result;
        }

        public void AddChild(FileSystemEntry entry)
        {
            children.Add(entry);
        }

        public void AddChildren(IEnumerable<FileSystemEntry> entries)
        {
            children.AddRange(entries);
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

        public FileSystemEntry? GetEntryFromPath(string path, bool createFolderStructure = false)
        {
            var pathComponents = path.Split(@"\", StringSplitOptions.RemoveEmptyEntries);

            if (pathComponents.Length == 0)
            {
                return this;
            }

            Folder currentFolder = this;

            for (int i = 0; i < pathComponents.Length; i++)
            {
                var component = pathComponents[i];
                var isLatestItem = i == pathComponents.Length - 1;

                var subFolder = currentFolder
                                    .Children
                                    .OfType<Folder>()
                                    .FirstOrDefault(child => child.Name.Equals(component, StringComparison.CurrentCultureIgnoreCase));

                if (subFolder == null)
                {
                    if (isLatestItem && !createFolderStructure)
                    {
                        //this is the last item. Perhaps it's a file

                        var file = currentFolder
                                    .Children
                                    .OfType<FileEntry>()
                                    .FirstOrDefault(child => child.Name.Equals(component, StringComparison.CurrentCultureIgnoreCase));

                        return file;
                    }
                    else
                    {
                        //not at the end, and this particular folder was not found

                        if (createFolderStructure)
                        {
                            subFolder = new Folder(component, currentFolder);
                        }
                        else
                        {
                            //folder not found
                            return null;
                        }
                    }
                }

                if (isLatestItem)
                {
                    //we found the subfolder they requested
                    return subFolder;
                }

                currentFolder = subFolder;
            }

            return null;
        }

        public override string ToString()
        {
            var result = $"{Name}";
            return result;
        }
    }
}
