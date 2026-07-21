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

        //for System.Text.Json (fields are populated via IncludeFields)
        [System.Text.Json.Serialization.JsonConstructor]
        public ArchiveEntry()
        {
            Path = "";
        }

        public ArchiveEntry(string path)
        {
            Path = path;
        }

        public bool IsFolder;
        public long Size;
        public DateTime Modified;
        public DateTime Created;
        public DateTime Accessed;
        public long? Offset;

        public override string ToString()
        {
            return Path;
        }
    }
}
