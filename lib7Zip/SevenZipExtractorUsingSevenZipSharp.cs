using Serilog;
using SevenZip;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace lib7Zip
{
    public class SevenZipExtractorUsingSevenZipSharp
    {
        readonly SevenZip.SevenZipExtractor Extractor;

        //Was hoping that we could get SevenZipExtractor to load the archive (which takes time) in the constructor, so that it would be more responsive when asked to extract a file from it.
        //But it still takes too long, causing an explorer.exe timeout.
        public SevenZipExtractorUsingSevenZipSharp(string archiveFilename)
        {
            var libraryPath = SevenZipUtility.SevenZipDll();
            SevenZipBase.SetLibraryPath(libraryPath);

            var format = DeduceFormat(archiveFilename);

            //If the format is not provided, throws: System.ArgumentException: 'Extension "img" is not a supported archive file name extension.'.
            //If the format is provided, we get further along. But when ExtractFile() is called, it returns:
            //Execution has failed due to an internal SevenZipSharp issue (0x800705aa / -2147023446). You might find more info at https://github.com/squid-box/SevenZipSharp/issues/, but this library is no longer actively supported
            Extractor = new SevenZip.SevenZipExtractor(archiveFilename, format);

            //force the extractor to load the archive
            Log.Information($"Initialising extractor for {archiveFilename}");
            Extractor.ExtractFile(0, Stream.Null);
            Log.Information($"Finished initialiasing extractor for {archiveFilename}");
        }

        public SevenZipExtractorUsingSevenZipSharp(Stream stream)
        {
            var libraryPath = SevenZipUtility.SevenZipDll();
            SevenZipBase.SetLibraryPath(libraryPath);

            var format = DeduceFormat(stream);
            Extractor = new SevenZip.SevenZipExtractor(stream, true, format);

            //force the extractor to load the archive
            Log.Information($"Initialising extractor for stream.");
            Extractor.ExtractFile(0, Stream.Null);
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
                                    var extractor = new SevenZip.SevenZipExtractor(stream, true, format);
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
                                    var extractor = new SevenZip.SevenZipExtractor(archiveFilename, format);
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
