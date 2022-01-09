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
        public List<FileSystemEntry> Children = new();

        public Folder CreateOrRetrieve(string path)
        {
            var folderEntry = GetEntryFromPath(path, true);

            if (folderEntry == null) throw new Exception($"Could not created folder: {path}");
            if (folderEntry is not Folder folder) throw new Exception($"Retrieved entry is not a folder for path: {path}");

            return folder;
        }

        public Folder(string name) : base(name)
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

            Folder? currentFolder = this;
            FileEntry? file = null;

            for (int i = 0; i < pathComponents.Length; i++)
            {
                var component = pathComponents[i];

                if (currentFolder == null) break;

                var subFolder = currentFolder
                                    .Children
                                    .OfType<Folder>()
                                    .FirstOrDefault(child => child.Name.Equals(component, StringComparison.CurrentCultureIgnoreCase));

                if (subFolder == null)
                {
                    if (i == pathComponents.Length - 1 && !createFolderStructure)
                    {
                        //this is the last item. Perhaps it's a file

                        file = currentFolder
                                    .Children
                                    .OfType<FileEntry>()
                                    .FirstOrDefault(child => child.Name.Equals(component, StringComparison.CurrentCultureIgnoreCase));
                    }
                    else
                    {
                        //not at the end, and this particular folder was not found

                        if (createFolderStructure)
                        {
                            subFolder = new Folder(component);
                            currentFolder?.Children.Add(subFolder);
                        }
                        else
                        {
                            currentFolder = null;
                            break;
                        }
                    }
                }

                currentFolder = subFolder;
            }

            FileSystemEntry? result;
            if (file == null)
            {
                result = currentFolder;
            }
            else
            {
                result = file;
            }

            return result;
        }

        public override string ToString()
        {
            var result = $"{Name}";
            return result;
        }
    }
}
