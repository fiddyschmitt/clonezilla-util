using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace libClonezilla.Cache.FileSystem
{
    public class FileSystemObject
    {
        public string? FullPath;
        public DateTime LastModifiedDate;
        public DateTime CreationDate;

        public override string ToString()
        {
            return FullPath ?? "";
        }
    }
}
