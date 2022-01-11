using Serilog;
using SevenZip;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace lib7Zip
{
    public class SevenZipExtractorEx
    {
        readonly SevenZipExtractor Extractor;

        //Was hoping that we could get SevenZipExtractor to load the archive (which takes time) in the constructor, so that it would be more responsive when asked to extract a file from it. But it still takes too long, causing an explorer.exe timeout.
        public SevenZipExtractorEx(string archiveFilename)
        {
            var libraryPath = SevenZipUtility.SevenZipDll();
            SevenZipBase.SetLibraryPath(libraryPath);

            var format = DeduceFormat(archiveFilename);
            Extractor = new SevenZipExtractor(archiveFilename, format);

            //force the extractor to load the archive
            Log.Information($"Initialising extractor for {archiveFilename}");
            Extractor.ExtractFile(0, Stream.Null);
            Log.Information($"Finished initialiasing extractor for {archiveFilename}");
        }

        public SevenZipExtractorEx(Stream stream)
        {
            var libraryPath = SevenZipUtility.SevenZipDll();
            SevenZipBase.SetLibraryPath(libraryPath);

            var format = DeduceFormat(stream);
            Extractor = new SevenZipExtractor(stream, format);

            //force the extractor to load the archive
            Log.Information($"Initialising extractor for stream.");
            Extractor.ExtractFile(@"keycloak-15.0.2\version.txt", Stream.Null);
            Log.Information($"Finished initialiasing extractor for stream.");
        }

        public void ExtractFile(string archiveEntryPath, Stream stream)
        {
            Extractor.ExtractFile(archiveEntryPath, stream);
        }

        public static InArchiveFormat DeduceFormat(Stream stream)
        {
            var result = Enum
                            .GetValues(typeof(InArchiveFormat))
                            .Cast<InArchiveFormat>()
                            .Select(format =>
                            {
                                uint fileCount = 0;
                                try
                                {
                                    var extractor = new SevenZipExtractor(stream, format);
                                    fileCount = extractor.FilesCount;
                                }
                                catch { }

                                return (format, fileCount);
                            })
                            .OrderByDescending(pair => pair.fileCount)
                            .Select(pair => pair.format)
                            .FirstOrDefault();

            if (result == 0) throw new Exception($"Could not deduce format for stream.");

            return result;
        }

        public static InArchiveFormat DeduceFormat(string archiveFilename)
        {
            var result = Enum
                            .GetValues(typeof(InArchiveFormat))
                            .Cast<InArchiveFormat>()
                            .Select(format =>
                            {
                                uint fileCount = 0;
                                try
                                {
                                    var extractor = new SevenZipExtractor(archiveFilename, format);
                                    fileCount = extractor.FilesCount;
                                }
                                catch { }

                                return (format, fileCount);
                            })
                            .OrderByDescending(pair => pair.fileCount)
                            .Select(pair => pair.format)
                            .FirstOrDefault();

            if (result == 0) throw new Exception($"Could not deduce format for archive: {archiveFilename}");

            return result;
        }
    }
}
