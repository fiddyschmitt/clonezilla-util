using lib7Zip;
using libCommon;
using MountDocushare.Streams;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util.Extractors
{
    public class ExtractorUsing7z : IExtractor
    {
        public ExtractorUsing7z(string archiveFilename)
        {
            ArchiveFilename = archiveFilename;
        }

        public string ArchiveFilename { get; }

        public Stream Extract(string pathInArchive)
        {
            /*
            var stream = new MemoryStream();
            //lock (realImageFile)    //No need to synchronise here, as the PartcloneStream already protects itself from concurrent access
            {
                SevenZipUtility.ExtractFileFromArchive(ArchiveFilename, pathInArchive, stream);
            }
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
            */

            var processStream = SevenZipUtility.ExtractFileFromArchive(ArchiveFilename, pathInArchive);
            var tempStorageStream = new MemoryStream();
            var result = new WaitForDataStream(processStream, tempStorageStream); //this will return data as soon as it arrives from the process

            return result;
        }
    }
}
