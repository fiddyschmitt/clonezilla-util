using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsingSevenZipSharp : IExtractor
    {
        readonly SevenZipExtractorUsingSevenZipSharp sevenZipExtractorEx;

        public ExtractorUsingSevenZipSharp(Stream archiveStream)
        {
            sevenZipExtractorEx = new SevenZipExtractorUsingSevenZipSharp(archiveStream);
        }

        public ExtractorUsingSevenZipSharp(string archiveFilename)
        {
            sevenZipExtractorEx = new SevenZipExtractorUsingSevenZipSharp(archiveFilename);
        }

        public Stream Extract(string pathInArchive)
        {
            var stream = new MemoryStream();
            sevenZipExtractorEx.ExtractFile(pathInArchive, stream);
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }
    }
}
