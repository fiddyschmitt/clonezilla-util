using lib7Zip;
using lib7Zip.Native;
using libCommon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace libClonezilla.Extractors
{
    /// <summary>
    /// Reads files from a partition using the in-process native 7-Zip engine (lib7zNative), which
    /// loads the bundled 7z.dll for the format handlers and does 7-Zip's own auto-detection / open.
    /// Replaces the 7zFM GUI automation.
    /// Not thread-safe: one open archive over one stream; calls are serialised here (and the caller
    /// also wraps this in a SynchronisedExtractor).
    /// </summary>
    public class SevenZipExtractorUsingNative : IExtractor, IFileListProvider, IDisposable
    {
        readonly Func<Stream> streamFactory;
        readonly string sevenZipDllPath;
        readonly object sync = new();

        SevenZipNativeArchive? archive;
        Dictionary<string, uint>? pathToIndex;

        public SevenZipExtractorUsingNative(Func<Stream> streamFactory, string sevenZipDllPath)
        {
            this.streamFactory = streamFactory;
            this.sevenZipDllPath = sevenZipDllPath;
        }

        // caller must hold 'sync'
        SevenZipNativeArchive GetArchive() =>
            archive ??= new SevenZipNativeArchive(streamFactory(), sevenZipDllPath, ownsStream: true);

        // caller must hold 'sync'
        List<ArchiveEntry> Enumerate()
        {
            var entries = GetArchive().GetEntries();
            var map = new Dictionary<string, uint>(entries.Count, StringComparer.Ordinal);
            var result = new List<ArchiveEntry>(entries.Count);
            foreach (var e in entries)
            {
                map[e.Path] = e.Index;
                result.Add(new ArchiveEntry(e.Path)
                {
                    IsFolder = e.IsDir,
                    Size = e.Size,
                    Modified = e.Modified ?? default,
                    Created = e.Created ?? default,
                    Accessed = e.Accessed ?? default,
                });
            }
            pathToIndex = map;
            return result;
        }

        public IEnumerable<ArchiveEntry> GetFileList()
        {
            lock (sync)
            {
                return Enumerate();
            }
        }

        public Stream Extract(string pathInArchive)
        {
            lock (sync)
            {
                var arc = GetArchive();
                if (pathToIndex == null) Enumerate();

                if (!pathToIndex!.TryGetValue(pathInArchive, out var index))
                    throw new FileNotFoundException($"Entry not found in partition: {pathInArchive}");

                // Extract fully to a delete-on-close temp file, then serve it (matches the old 7zFM
                // behaviour). Streaming-as-decoded can be layered on later if needed.
                var tempFilename = TempUtility.GetTempFilename(true);
                var temp = new FileStream(tempFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
                try
                {
                    arc.ExtractTo(index, temp);
                    temp.Seek(0, SeekOrigin.Begin);
                    return temp;
                }
                catch
                {
                    temp.Dispose();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                archive?.Dispose();
                archive = null;
            }
        }
    }
}
