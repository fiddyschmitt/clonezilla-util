using lib7Zip;
using libDokan;
using libDokan.VFS;
using libDokan.VFS.Files;
using Serilog;
using SevenZip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace clonezilla_util.VFS
{
    public class SevenZipBackedFileEntry : FileEntry
    {
        public SevenZipBackedFileEntry(ArchiveEntry archiveEntry, Func<ArchiveEntry, Stream> extractor) : base(Path.GetFileName(archiveEntry.Path))
        {
            ArchiveEntry = archiveEntry;
            Extractor = extractor;
        }

        public ArchiveEntry ArchiveEntry { get; }

        [JsonIgnore]
        public Func<ArchiveEntry, Stream>? Extractor { get; set; }

        public override Stream GetStream()
        {
            if (Extractor == null) throw new Exception($"{nameof(SevenZipBackedFileEntry)}: Extractor not initialized.");

            var stream = Extractor(ArchiveEntry);
            return stream;






            //Uses SevenZipExtractorEx.cs, which is a wrapper for Squid-Box.SevenZipSharp.
            //Was hoping that we could get SevenZipExtractor to load the archive (which takes time) in the constructor, so that it would be more responsive when asked to extract a file from it. But it still takes too long, causing an explorer.exe timeout.
            /*
            lock (Extractor)
            {
                var stream = new MemoryStream();
                try
                {
                    Extractor.ExtractFile(ArchiveEntry.Path, stream);
                    stream.Seek(0, SeekOrigin.Begin);
                } catch (Exception ex)
                {
                    Log.Error($"{ex}");
                }
                return stream;
            }
            */












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
            //return stream;
        }

        public override string ToString()
        {
            var result = $"{Name}";
            return result;
        }
    }
}
