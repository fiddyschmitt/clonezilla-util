using lib7Zip;
using System;
using System.Collections.Generic;
using System.Text;

namespace libClonezilla.Extractors
{
    public interface IFileListProvider
    {
        IEnumerable<ArchiveEntry> GetFileList();
    }
}
