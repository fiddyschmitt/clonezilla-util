using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.VFS.Folders
{
    public class UnlistedFolder : Folder
    {
        public UnlistedFolder(string name, Folder? parent) : base(name, parent)
        {
        }
    }
}
