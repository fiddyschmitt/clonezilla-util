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
    public class ExtractorUsing7zip : IExtractor, IFileListProvider
    {
        public string? ArchiveFilename { get; protected set; }

        protected IExtractor? extractor;

        public ExtractorUsing7zip(string path)
        {
            ArchiveFilename = path;
            extractor = DetermineOptimalExtractor(path);
        }

        public bool Initialise(string path)
        {
            ArchiveFilename = path;

            var result = SevenZipUtility.IsArchive(path, true, CancellationToken.None);

            if (result)
            {
                extractor ??= DetermineOptimalExtractor(path);
            }

            return result;
        }

        static IExtractor DetermineOptimalExtractor(string archiveFilename)
        {
            var originFriendlyName = $"[{Path.GetFileName(archiveFilename)}]";
            IExtractor extractor;

            //Do a performance test. If the archive can be opened quickly, then use 7z.exe which is slow but reliable. If it takes a long time, then use 7zFM which is fast but less reliable.
            var performanceTestTimeout = TimeSpan.FromSeconds(10);
            var performanceTestCancellationToken = new CancellationTokenSource();

            var performanceTestTask = Task.Factory.StartNew(() =>
            {
                Log.Information($"{originFriendlyName} Determining optimal way to extract files from this partition.");
                var testStart = DateTime.Now;

                SevenZipUtility.IsArchive(archiveFilename, true, performanceTestCancellationToken.Token);

                if (performanceTestCancellationToken.IsCancellationRequested)
                {
                    Log.Debug($"{originFriendlyName} Test did not finish within the {performanceTestTimeout.TotalSeconds:N0)} second timeout.");
                }
                else
                {
                    var testDuration = DateTime.Now - testStart;
                    Log.Debug($"{originFriendlyName} Archive opened in {testDuration.TotalSeconds:N1} seconds.");
                }
            }, TaskCreationOptions.LongRunning);

            bool use7z;
            if (Task.WhenAny(performanceTestTask, Task.Delay(performanceTestTimeout)).Result == performanceTestTask)
            {
                // task completed within timeout
                use7z = true;
            }
            else
            {
                // timed out. Cancel the test
                performanceTestCancellationToken.Cancel();
                performanceTestTask.Wait();

                use7z = false;
            }

            if (use7z)
            {
                Log.Information($"{originFriendlyName} 7z.exe will be used to extract files from this partition.");

                //Extractor which uses 7z.exe.
                //It runs the process and returns its stdout straight away, so it's non-blocking.
                //Works fine, except it's too slow when extracting from a large archive, causing explorer.exe to assume the file isn't available.
                extractor = new ExtractorUsing7z(archiveFilename);

                //This actually causes errors for FLP, because FLP uses more threads than are being served
                /*
                var extractors = new List<IExtractor>();
                for (int i = 0; i < 8; i++)
                {
                    var newExtractor = new ExtractorUsing7z(ImageFileEntry.FullPath);
                    extractors.Add(newExtractor);
                }

                extractor = new MultiExtractor(extractors, true);
                */
            }
            else
            {
                Log.Information($"{originFriendlyName} 7zFM.exe will be used to extract files from this partition.");

                var instanceCount = 4;
                Log.Information($"{originFriendlyName} Starting {instanceCount:N0} instances of 7zFM.exe.");

                //Extractor which uses the 7-Zip File Manager
                //Opens the archive here, up front. Subsequent extracts are quick.
                //Creating them in parallel is ideal, because the data each needs is available in the memory cache.
                var extractors = Enumerable
                                    .Range(1, instanceCount)
                                    .AsParallel()
                                    .WithDegreeOfParallelism(2)     //Loading 4 in parallel ocassionally causes one 7zFM instance to show 'Insufficient system resources'
                                    .Select(_ => new ExtractorUsing7zFM(archiveFilename))
                                    .OfType<IExtractor>()
                                    .ToList();

                extractor = new MultiExtractor(extractors, true);
            }

            /*
            //Was hoping that these libraries could load the archive (which takes time) in the constructor, so that it would be more responsive when asked to extract a file from it.
            var partitionDetails = container
                                    .Partitions
                                    .FirstOrDefault(partition => partition.PartitionName.Equals(partitionName)) ?? throw new Exception($"Could not load details for {partitionName}.");

            var extractors = new List<IExtractor>();
            for (int i = 0; i < 4; i++)
            {
                //Uses Squid-Box.SevenZipSharp. Throws an exception when trying to extract files:
                //Execution has failed due to an internal SevenZipSharp issue (0x800705aa / -2147023446). You might find more info at https://github.com/squid-box/SevenZipSharp/issues/, but this library is no longer actively supported
                //var newExtractor = new ExtractorUsingSevenZipSharp(ImageFileEntry.FullPath);

                //Uses SevenZipExtractor, but throws SevenZipExtractor.SevenZipException: 'partition0.img is not a known archive type'
                //var newExtractor = new ExtractorUsingSevenZipExtractor(ImageFileEntry.FullPath);

                //Uses SevenZipExtractor, but throws SevenZipExtractor.SevenZipException: 'Unable to guess format automatically'
                var newExtractor = new ExtractorUsingSevenZipExtractor(File.OpenRead(ImageFileEntry.FullPath));

                extractors.Add(newExtractor);
            }

            extractor = new MultiExtractor(extractors, true);
            */

            return extractor;
        }

        public Stream Extract(string path)
        {
            if (extractor == null)
            {
                return Stream.Null;
            }

            var result = extractor.Extract(path);
            return result;
        }

        public IEnumerable<ArchiveEntry> GetFileList()
        {
            if (ArchiveFilename == null)
            {
                return [];
            }

            var result = SevenZipUtility.GetArchiveEntries(ArchiveFilename, false, false);
            return result;
        }
    }
}
