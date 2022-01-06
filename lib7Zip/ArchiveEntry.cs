using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace lib7Zip
{
    public class ArchiveEntry
    {
        public string Path;

        public ArchiveEntry(string path)
        {
            Path = path;
        }

        public bool IsFolder;
        public long Size;
        public DateTime Modified;
        public DateTime Created;
        public DateTime Accessed;
    }
}
