using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsingSevenZipSharp : IExtractor, IFileListProvider
    {
        protected SevenZipExtractorUsingSevenZipSharp sevenZipExtractorEx;

        public ExtractorUsingSevenZipSharp(string path)
        {
            sevenZipExtractorEx = new SevenZipExtractorUsingSevenZipSharp(path);
        }

        public Stream Extract(string path)
        {
            var stream = new MemoryStream();
            sevenZipExtractorEx.ExtractFile(path, stream);
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }

        public IEnumerable<ArchiveEntry> GetFileList()
        {
            var result = sevenZipExtractorEx
                            .GetEntries()
                            .Select(data => new ArchiveEntry(data.FileName)
                            {
                                Created = data.CreationTime,
                                Accessed = data.LastAccessTime,
                                Modified = data.LastWriteTime,

                                IsFolder = data.IsDirectory,
                                Offset = 0,
                                Path = "",
                                Size = (long)data.Size
                            });

            return result;
        }
    }
}
