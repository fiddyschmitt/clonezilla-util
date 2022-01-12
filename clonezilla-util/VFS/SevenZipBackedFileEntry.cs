using lib7Zip;
using libDokan;
using libDokan.VFS;
using libDokan.VFS.Files;
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
        public SevenZipBackedFileEntry() : base("")
        {
            PathInArchive = "";
        }

        public SevenZipBackedFileEntry(ArchiveEntry archiveEntry, Func<string, Stream> extractor) : base(Path.GetFileName(archiveEntry.Path))
        {
            Extractor = extractor;

            Created = archiveEntry.Created;
            Accessed = archiveEntry.Accessed;
            Modified = archiveEntry.Modified;
            Length = archiveEntry.Size;

            PathInArchive = archiveEntry.Path;
        }


        [IgnoreDataMember]
        public Func<string, Stream>? Extractor { get; set; }
        public string PathInArchive { get; set; }

        public override Stream GetStream()
        {
            if (Extractor == null) throw new Exception($"{nameof(SevenZipBackedFileEntry)}: Extractor not initialized.");

            var stream = Extractor(PathInArchive);
            return stream;
        }

        public override string ToString()
        {
            var result = $"{Name}";
            return result;
        }
    }
}
