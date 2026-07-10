using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace libClonezilla.Extractors
{
    public static class DetermineExtractor
    {
        // Mount serves concurrent OS file requests, so it pre-warms several workers (each its own open
        // archive) up front to avoid read-time stalls. Listing only enumerates once, so it needs just one.
        public const int MountWorkerCount = 4;
        public const int ListingWorkerCount = 1;

        /// <summary>
        /// Builds an extractor backed by the in-process native 7-Zip engine (lib7zNative), which loads
        /// the bundled 7z.dll for handlers and does 7-Zip's own format auto-detection. This supersedes
        /// the old perf-test that chose between slow-but-reliable 7z.exe and the fragile 7zFM GUI automation.
        /// </summary>
        /// <param name="partitionStreamFactory">Creates a fresh seekable stream over the partition's
        /// decompressed content (e.g. a SharedStream.CreateView over Partition.FullPartitionImage).</param>
        /// <param name="instanceCount">How many workers to pre-warm. Use <see cref="MountWorkerCount"/>
        /// for mounting (concurrent reads) and <see cref="ListingWorkerCount"/> for listing only.</param>
        public static IExtractor FindExtractor(Func<Stream> partitionStreamFactory, int instanceCount = MountWorkerCount)
        {
            var sevenZipDll = SevenZipUtility.SevenZipDll();
            return new NativeExtractorPool(partitionStreamFactory, sevenZipDll, instanceCount);
        }
    }
}
