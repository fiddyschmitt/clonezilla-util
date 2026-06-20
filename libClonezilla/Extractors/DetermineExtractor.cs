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
        /// <summary>
        /// Builds an extractor backed by the in-process native 7-Zip engine (lib7zNative), which loads
        /// the bundled 7z.dll for handlers and does 7-Zip's own format auto-detection. This supersedes
        /// the old perf-test that chose between slow-but-reliable 7z.exe and the fragile 7zFM GUI automation.
        /// </summary>
        /// <param name="partitionStreamFactory">Creates a fresh seekable stream over the partition's
        /// decompressed content (e.g. an IndependentStream over Partition.FullPartitionImage).</param>
        public static IExtractor FindExtractor(Func<Stream> partitionStreamFactory)
        {
            var sevenZipDll = SevenZipUtility.SevenZipDll();

            // Pool several workers so the VFS can service concurrent OS requests for different files
            // without one extraction blocking the others (which otherwise triggers Dokan/Explorer
            // "Insufficient system resources"/timeout errors). Workers open lazily.
            const int instanceCount = 4;
            return new NativeExtractorPool(partitionStreamFactory, sevenZipDll, instanceCount);
        }
    }
}
