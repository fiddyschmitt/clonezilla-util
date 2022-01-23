using libDokan.Processes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.VFS.Folders
{
    public class RestrictedFolderByPID : Folder
    {
        public RestrictedFolderByPID(string name, Folder? parent, Func<ProcInfo, bool> isProcessPermitted) : base(name, parent)
        {
            IsProcessPermitted = isProcessPermitted;
        }

        public Func<ProcInfo, bool> IsProcessPermitted { get; }
    }
}
