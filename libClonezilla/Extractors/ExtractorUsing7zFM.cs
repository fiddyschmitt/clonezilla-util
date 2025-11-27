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

        public ExtractorUsing7zFM(string path)
        {
            ArchiveFilename = path;
            FileManagerExtractor = new SevenZipExtractorUsing7zFM(path);
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
            var result = SevenZipUtility.GetArchiveEntries(ArchiveFilename, false, false);
            return result;
        }
    }
}
