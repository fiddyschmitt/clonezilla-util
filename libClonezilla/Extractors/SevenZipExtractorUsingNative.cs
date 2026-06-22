using lib7Zip.Native;
using System;
using System.Collections.Generic;
using System.IO;

namespace libClonezilla.Extractors
{
    /// <summary>
    /// A single in-process native 7-Zip worker: one open archive over one view of the partition's
    /// decompressed stream. Serves a file as a seekable, on-demand stream (no extraction, no temp
    /// file) via <see cref="SevenZipNativeArchive.OpenItemStream"/>.
    ///
    /// Opening the archive parses the filesystem metadata (e.g. the NTFS $MFT) which is slow over a
    /// compressed backing stream, so workers are opened up front (see <see cref="EnsureOpen"/>) by
    /// <see cref="NativeExtractorPool"/> - never lazily inside a Dokan callback.
    ///
    /// Not thread-safe: one open archive drives one input stream, so only one item stream may be
    /// alive at a time. The pool enforces this by checking out one worker per open stream.
    /// </summary>
    public sealed class SevenZipExtractorUsingNative : IDisposable
    {
        readonly Func<Stream> streamFactory;
        readonly string sevenZipDllPath;
        SevenZipNativeArchive? archive;

        public SevenZipExtractorUsingNative(Func<Stream> streamFactory, string sevenZipDllPath)
        {
            this.streamFactory = streamFactory;
            this.sevenZipDllPath = sevenZipDllPath;
        }

        /// <summary>Opens the archive (parsing filesystem metadata) if not already open. Call up front.</summary>
        public SevenZipNativeArchive EnsureOpen() =>
            archive ??= new SevenZipNativeArchive(streamFactory(), sevenZipDllPath, ownsStream: true);

        public IReadOnlyList<NativeArchiveEntry> GetEntries() => EnsureOpen().GetEntries();

        /// <summary>
        /// Opens item <paramref name="index"/> as a seekable read-only stream. <paramref name="onClosed"/>
        /// runs when that stream is disposed (used to return this worker to its pool).
        /// </summary>
        public Stream OpenItemStream(uint index, long size, Action onClosed) =>
            EnsureOpen().OpenItemStream(index, size, onClosed);

        public void Dispose()
        {
            archive?.Dispose();
            archive = null;
        }
    }
}
