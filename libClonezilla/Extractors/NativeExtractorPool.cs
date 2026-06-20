using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace libClonezilla.Extractors
{
    /// <summary>
    /// Pools several <see cref="SevenZipExtractorUsingNative"/> workers (each its own open archive over
    /// its own stream view) behind a <see cref="MultiExtractor"/>, so concurrent OS file requests are
    /// serviced in parallel instead of queuing behind one extraction - which is what avoids Dokan/Explorer
    /// "Insufficient system resources"/timeout errors. Workers open lazily, so low concurrency only ever
    /// opens one archive.
    /// </summary>
    public class NativeExtractorPool : IExtractor, IFileListProvider, IDisposable
    {
        readonly List<SevenZipExtractorUsingNative> workers;
        readonly MultiExtractor pool;

        public NativeExtractorPool(Func<Stream> streamFactory, string sevenZipDllPath, int instanceCount)
        {
            workers = Enumerable
                        .Range(0, instanceCount)
                        .Select(_ => new SevenZipExtractorUsingNative(streamFactory, sevenZipDllPath))
                        .ToList();

            // forceFullRead: false - each worker's Extract already returns a fully-extracted, seekable
            // temp stream, so the worker is idle the moment Extract returns.
            pool = new MultiExtractor(workers.Cast<IExtractor>().ToList(), forceFullRead: false);
        }

        // List once via any worker; every worker opens identical content, so the entry order (and thus
        // the path->index mapping each worker builds) is the same across them.
        public IEnumerable<ArchiveEntry> GetFileList() => workers[0].GetFileList();

        public Stream Extract(string path) => pool.Extract(path);

        public void Dispose() => workers.ForEach(w => w.Dispose());
    }
}
