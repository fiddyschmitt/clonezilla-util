using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace lib7Zip
{
    public class ArchiveEntry
    {
        public string ArchiveFilename;
        public string Name;

        public bool IsFolder;
        public long Size;
        public DateTime Modified;
        public DateTime Created;
        public DateTime Accessed;
    }
}
