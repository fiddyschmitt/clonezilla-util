using lib7Zip;
using libDokan;
using libDokan.VFS;
using libDokan.VFS.Files;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util.VFS
{
    public class SevenZipBackedFileEntry : FileEntry
    {
        public SevenZipBackedFileEntry(string realArchiveFilename, ArchiveEntry archiveEntry) : base(Path.GetFileName(archiveEntry.Path))
        {
            RealArchiveFilename = realArchiveFilename;
            ArchiveEntry = archiveEntry;
        }

        public string RealArchiveFilename { get; }
        public ArchiveEntry ArchiveEntry { get; }

        public override Stream GetStream()
        {
            Stream stream;
            //lock (vfs)
            {
                stream = new MemoryStream();
                Log.Debug($"Extracting {ArchiveEntry.Path} from {RealArchiveFilename}");
                SevenZipUtility.ExtractFileFromArchive(RealArchiveFilename, ArchiveEntry.Path, stream);
                Log.Debug($"Finished extracting {ArchiveEntry.Path} from {RealArchiveFilename}");
            }
            stream.Seek(0, SeekOrigin.Begin);

            //Works, but not to think about how to clean up temp files reliably, particularly given that multiple instances of the program can be running
            //var tempFilename = Path.GetRandomFileName();
            //tempFilename = Path.Combine(clonezillaCacheManager.TempFolder, tempFilename);
            //File.Create(tempFilename).Close();
            //File.SetAttributes(tempFilename, FileAttributes.Temporary); //not sure if this is needed, given that we use DeleteOnClose in the next call. But according to its doco, it gives a hint to the OS to mainly keep it in memory.
            //                                                            //var tempFileStream = new FileStream(tempFilename, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose); //DeleteOnClose doesn't play nicely with the subsequent read.
            //var tempFileStream = new FileStream(tempFilename, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.ReadWrite);

            //lock (vfs)
            //{
            //    Log.Debug($"Extracting {archiveEntry.Path} from {realImageFile}");
            //    SevenZipUtility.ExtractFileFromArchive(realImageFile, archiveEntry.Path, tempFileStream);
            //    Log.Debug($"Finished extracting {archiveEntry.Path} from {realImageFile}");
            //}

            //var tempFilenameStr = extractedLookup[archiveEntry.Path];
            //var stream = File.Open(tempFilenameStr, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);

            return stream;
        }
    }
}
