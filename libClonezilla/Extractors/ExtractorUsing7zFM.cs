using lib7Zip;
using libCommon;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsing7zFM(string archiveFilename) : IExtractor
    {
        readonly SevenZipExtractorUsing7zFM FileManagerExtractor = new(archiveFilename);

        public Stream Extract(string pathInArchive)
        {
            var tempFolder = TempUtility.GetTemporaryDirectory();

            FileManagerExtractor.ExtractFile(pathInArchive, tempFolder);

            var tempFilename = Directory.GetFiles(tempFolder).First();

            Stream result = File.OpenRead(tempFilename); ;

            return result;
        }
    }
}
