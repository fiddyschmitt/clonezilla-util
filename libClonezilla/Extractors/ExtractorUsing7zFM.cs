using lib7Zip;
using libCommon;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsing7zFM : IExtractor, IFileListProvider
    {
        readonly SevenZipExtractorUsing7zFM FileManagerExtractor;

        public string ArchiveFilename { get; }

        public ExtractorUsing7zFM(string archiveFilename)
        {
            FileManagerExtractor = new(archiveFilename);
            ArchiveFilename = archiveFilename;
        }

        public bool Initialise(string path)
        {
            var result = SevenZipUtility.IsArchive(path, true, CancellationToken.None);
            return result;
        }

        public Stream Extract(string path)
        {
            var tempFolder = TempUtility.GetTemporaryDirectory();

            FileManagerExtractor.ExtractFile(path, tempFolder);

            var tempFilename = Directory.GetFiles(tempFolder).First();

            Stream result = File.OpenRead(tempFilename); ;

            return result;
        }
        public IEnumerable<ArchiveEntry> GetFileList()
        {
            if (ArchiveFilename == null)
            {
                return [];
            }

            var result = SevenZipUtility.GetArchiveEntries(ArchiveFilename, false, false);
            return result;
        }
    }
}
