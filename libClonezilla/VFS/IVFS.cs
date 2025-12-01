using libDokan.VFS.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.VFS
{
    public interface IVFS
    {
        public Folder CreateTempFolder();

        public Lazy<RootFolder> RootFolder { get; }
    }
}
