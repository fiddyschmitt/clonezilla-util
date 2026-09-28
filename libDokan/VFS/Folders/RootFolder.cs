using libDokan.VFS.Files;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.VFS.Folders
{
    public class RootFolder : Folder
    {
        public RootFolder(string mountPoint) : base("", null)
        {
            MountPoint = mountPoint;
        }

        public string MountPoint { get; }

        //The entry at `path` (Dokan's "\folder\file" form), or null. The last component may be a folder or a file; every
        //earlier one must be a folder. (A requesting-PID parameter, read only by commented-out restriction code, and a
        //create-missing-folders mode, used only by a commented-out method, were removed as dead code.)
        public FileSystemEntry? GetEntryFromPath(string path)
        {
            var pathComponents = path.Split(@"\", StringSplitOptions.RemoveEmptyEntries);

            if (pathComponents.Length == 0)
            {
                return this;
            }

            Folder currentFolder = this;
            for (int i = 0; i < pathComponents.Length; i++)
            {
                //O(1) lookup; GetChild returns the first child with this name (case-insensitive), folder or file
                var match = currentFolder.GetChild(pathComponents[i]);

                if (i == pathComponents.Length - 1) return match;
                if (match is not Folder subFolder) return null;   //missing, or a file used as a folder

                currentFolder = subFolder;
            }

            return null;
        }
    }
}
