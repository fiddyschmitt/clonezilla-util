using Serilog;
using SevenZip;
using SevenZipExtractor;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace lib7Zip
{
    public class SevenZipExtractorUsingSevenZipExtractor
    {
        readonly ArchiveFile archiveFile;

        public SevenZipExtractorUsingSevenZipExtractor(string archiveFilename)
        {
            archiveFile = new ArchiveFile(archiveFilename);
        }

        public SevenZipExtractorUsingSevenZipExtractor(Stream stream)
        {
            archiveFile = new ArchiveFile(stream);
        }

        public void ExtractFile(string archiveEntryPath, Stream stream)
        {
            var entry = archiveFile
                        .Entries
                        .FirstOrDefault(entry => entry.FileName.Equals(archiveEntryPath, StringComparison.OrdinalIgnoreCase));

            entry?.Extract(stream);
        }

        public IEnumerable<Entry> GetEntries()
        {
            return archiveFile.Entries;
        }
    }
}
