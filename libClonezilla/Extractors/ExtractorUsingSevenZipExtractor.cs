using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsingSevenZipExtractor : IExtractor, IFileListProvider
    {
        protected SevenZipExtractorUsingSevenZipExtractor? sevenZipExtractorEx;

        public bool Initialise(string path)
        {
            sevenZipExtractorEx = new SevenZipExtractorUsingSevenZipExtractor(path);

            var success = sevenZipExtractorEx.GetEntries().Any();
            return success;
        }

        public Stream Extract(string path)
        {
            if (sevenZipExtractorEx == null)
            {
                return Stream.Null;
            }

            var stream = new MemoryStream();
            sevenZipExtractorEx.ExtractFile(path, stream);
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }

        public IEnumerable<ArchiveEntry> GetFileList()
        {
            var result = sevenZipExtractorEx?
                            .GetEntries()
                            .Select(entry => new ArchiveEntry(entry.FileName)
                            {
                                Created = entry.CreationTime,
                                Accessed = entry.LastAccessTime,
                                Modified = entry.LastWriteTime,

                                IsFolder = entry.IsFolder,
                                Offset = 0,
                                Size = (long)entry.Size
                            }) ?? [];

            return result;
        }
    }
}
