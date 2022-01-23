using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsingSevenZipExtractorLibrary : IExtractor
    {
        readonly SevenZipExtractorEx sevenZipExtractorEx;

        public ExtractorUsingSevenZipExtractorLibrary(Stream archiveStream)
        {
            sevenZipExtractorEx = new SevenZipExtractorEx(archiveStream);
        }

        public ExtractorUsingSevenZipExtractorLibrary(string archiveFilename)
        {
            sevenZipExtractorEx = new SevenZipExtractorEx(archiveFilename);
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
