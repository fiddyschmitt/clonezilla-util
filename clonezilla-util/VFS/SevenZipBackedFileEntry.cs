using clonezilla_util.Extractors;
using lib7Zip;
using libDokan;
using libDokan.VFS;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using Serilog;
using SevenZip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace clonezilla_util.VFS
{
    public class SevenZipBackedFileEntry : FileEntry
    {
        //for Json deserializer
        public SevenZipBackedFileEntry() : base("", null)
        {
            PathInArchive = "";
        }

        public SevenZipBackedFileEntry(ArchiveEntry archiveEntry, Folder? parent, IExtractor extractor) : base(Path.GetFileName(archiveEntry.Path), parent)
        {
            Extractor = extractor;

            Created = archiveEntry.Created;
            Accessed = archiveEntry.Accessed;
            Modified = archiveEntry.Modified;
            Length = archiveEntry.Size;

            PathInArchive = archiveEntry.Path;
        }


        [IgnoreDataMember]
        public IExtractor? Extractor { get; set; }
        public string PathInArchive { get; set; }

        public override Stream GetStream()
        {
            if (Extractor == null) throw new Exception($"{nameof(SevenZipBackedFileEntry)}: Extractor not initialized.");

            var stream = Extractor.Extract(PathInArchive);
            return stream;
        }

        public override string ToString()
        {
            var result = $"{Name}";
            return result;
        }
    }
}
