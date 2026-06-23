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
    /// <see cref="Extract"/> returns a seekable, on-demand stream (no extraction, no temp file) that holds
    /// NO worker - it borrows one (<see cref="TakeWorker"/>/<see cref="ReturnWorker"/>) only for the
    /// duration of each Read. So opening a file is instant and never blocks (any number of files can be
    /// open at once); the pool size caps concurrent *reads*, not open handles. This avoids a slow/blocked
    /// Dokan CreateFile (which would surface as 0x800705AA once more than <c>instanceCount</c> files were
    /// open under the old per-handle-checkout model).
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

            //The returned stream holds no worker, so opening a file is instant and never blocks - any
            //number of files can be open concurrently. A worker is borrowed only per Read.
            return new PooledNativeItemStream(this, item.Index, item.Size);
        }

        //Borrow a worker for a single read. Blocks only while every worker is mid-read (brief), never
        //while files are merely open. Used by PooledNativeItemStream.
        internal SevenZipExtractorUsingNative TakeWorker() => available.Take();

        internal void ReturnWorker(SevenZipExtractorUsingNative worker)
        {
            try
            {
                available.Add(worker);
            }
            catch (ObjectDisposedException) { } //pool torn down (unmount) while a read was in flight
            catch (InvalidOperationException) { } //adding has been marked complete
        }

        public void Dispose()
        {
            available.Dispose();
            workers.ForEach(w => w.Dispose());
        }
    }
}
