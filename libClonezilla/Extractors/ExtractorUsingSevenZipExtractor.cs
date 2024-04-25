using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsingSevenZipExtractor : IExtractor
    {
        readonly SevenZipExtractorUsingSevenZipExtractor sevenZipExtractorEx;

        public ExtractorUsingSevenZipExtractor(Stream archiveStream)
        {
            sevenZipExtractorEx = new SevenZipExtractorUsingSevenZipExtractor(archiveStream);
        }

        public ExtractorUsingSevenZipExtractor(string archiveFilename)
        {
            sevenZipExtractorEx = new SevenZipExtractorUsingSevenZipExtractor(archiveFilename);
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
