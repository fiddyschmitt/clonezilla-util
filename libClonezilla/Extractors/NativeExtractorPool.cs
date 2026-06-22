using lib7Zip;
using lib7Zip.Native;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace libClonezilla.Extractors
{
    /// <summary>
    /// A fixed pool of native 7-Zip workers (each its own open archive over its own view of the
    /// partition's decompressed stream). All workers are opened UP FRONT (constructor) so the slow
    /// filesystem-metadata parse (e.g. the NTFS $MFT over a compressed stream) happens at mount time,
    /// never inside a Dokan callback (whose ~20s timeout would otherwise surface as
    /// "Insufficient system resources").
    ///
    /// <see cref="Extract"/> returns a seekable, on-demand stream (no extraction, no temp file) and
    /// checks out a worker for that stream's whole lifetime, returning it only when the stream is
    /// disposed. So at most <c>instanceCount</c> files can be open concurrently (further opens block
    /// until a worker frees up) - the same model as the previous 4-instance 7zFM approach.
    /// </summary>
    public class NativeExtractorPool : IExtractor, IFileListProvider, IDisposable
    {
        readonly List<SevenZipExtractorUsingNative> workers;
        readonly BlockingCollection<SevenZipExtractorUsingNative> available;
        readonly List<ArchiveEntry> fileList;
        readonly Dictionary<string, (uint Index, long Size)> pathToItem;

        public NativeExtractorPool(Func<Stream> streamFactory, string sevenZipDllPath, int instanceCount)
        {
            workers = Enumerable
                        .Range(0, instanceCount)
                        .Select(_ => new SevenZipExtractorUsingNative(streamFactory, sevenZipDllPath))
                        .ToList();

            IReadOnlyList<NativeArchiveEntry> entries;
            try
            {
                // Open every worker up front. Sequentially, so the first worker warms the shared decompression
                // cache (the expensive $MFT read) and the rest open against the warm cache.
                foreach (var worker in workers)
                {
                    worker.EnsureOpen();
                }

                // The path->index mapping is identical across workers (same content, same enumeration order),
                // so build it once from the first worker and share it.
                entries = workers[0].GetEntries();
            }
            catch
            {
                // Don't leak the partially-opened native archives if any worker failed to open
                // (e.g. NotAnArchiveException for a partition with no filesystem). The caller decides
                // whether that's fatal.
                workers.ForEach(w => w.Dispose());
                throw;
            }
            fileList = new List<ArchiveEntry>(entries.Count);
            pathToItem = new Dictionary<string, (uint, long)>(entries.Count, StringComparer.Ordinal);
            foreach (var e in entries)
            {
                pathToItem[e.Path] = (e.Index, e.Size);
                fileList.Add(new ArchiveEntry(e.Path)
                {
                    IsFolder = e.IsDir,
                    Size = e.Size,
                    Modified = e.Modified ?? default,
                    Created = e.Created ?? default,
                    Accessed = e.Accessed ?? default,
                });
            }

            available = new BlockingCollection<SevenZipExtractorUsingNative>(new ConcurrentQueue<SevenZipExtractorUsingNative>());
            workers.ForEach(available.Add);
        }

        public IEnumerable<ArchiveEntry> GetFileList() => fileList;

        public Stream Extract(string pathInArchive)
        {
            if (!pathToItem.TryGetValue(pathInArchive, out var item))
                throw new FileNotFoundException($"Entry not found in partition: {pathInArchive}");

            var worker = available.Take(); //blocks until a worker is free
            try
            {
                //the worker stays checked out until the returned stream is disposed
                return worker.OpenItemStream(item.Index, item.Size, onClosed: () => available.Add(worker));
            }
            catch
            {
                available.Add(worker); //opening the stream failed - return the worker immediately
                throw;
            }
        }

        public void Dispose()
        {
            available.Dispose();
            workers.ForEach(w => w.Dispose());
        }
    }
}
