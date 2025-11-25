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
using System.Threading;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class ExtractorUsing7z : IExtractor, IFileListProvider
    {
        public string ArchiveFilename { get; protected set; }

        public ExtractorUsing7z(string archiveFilename)
        {
            ArchiveFilename = archiveFilename;
        }

        public bool Initialise(string path)
        {
            ArchiveFilename = path;

            var result = SevenZipUtility.IsArchive(path, true, CancellationToken.None);
            return result;
        }

        public Stream Extract(string path)
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

            var processStream = SevenZipUtility.ExtractFileFromArchive(ArchiveFilename, path);

            //var tempStorageStream = new MemoryStream();   //can't use a MemoryStream because it has a limit of 2GB
            var tempFilename = TempUtility.GetTempFilename(true);
            var tempStorageStream = new FileStream(tempFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);

            var result = new SiphonStream(processStream, tempStorageStream); //this will return data as soon as it arrives from the process

            /*
            processStream.CopyTo(tempStorageStream);
            tempStorageStream.Seek(0, SeekOrigin.Begin);
            var result = tempStorageStream;
            */

            //processStream.CopyTo(tempStorageStream);
            //var result = tempStorageStream;

            return result;
        }

        public IEnumerable<ArchiveEntry> GetFileList()
        {
            var result = SevenZipUtility.GetArchiveEntries(ArchiveFilename, false, false);
            return result;
        }
    }
}
