using System;
using System.IO;
using static lib7Zip.Native.SevenZipNativeInterop;

namespace lib7Zip.Native
{
    /// <summary>
    /// A seekable, read-only Stream over a single archive item, backed by 7-Zip's
    /// IInArchiveGetStream (e.g. NTFS file data mapped to partition clusters, decompressed on
    /// demand). No extraction and no temp file - reads pull data directly as requested.
    ///
    /// NOT thread-safe, and reads drive the owning archive's single input stream, so only one
    /// NativeItemStream may be alive per <see cref="SevenZipNativeArchive"/> at a time. The caller
    /// (the extractor pool) enforces this: a read borrows a worker, opens an item stream on it, and
    /// disposes the item stream before returning the worker.
    ///
    /// <paramref name="onDispose"/> runs after the native item stream is closed by an explicit Dispose.
    /// A stream that is never disposed is still closed by its SafeHandle's finalizer, but the callback
    /// does not run then: it would call back into its owner from the finalizer thread.
    /// </summary>
    public sealed class NativeItemStream : Stream
    {
        readonly ItemStreamHandle handle;
        readonly long length;
        long position;
        readonly Action? onDispose;

        //The archive's Read/Seek callbacks are delegates it owns; the native archive calls them for as long as
        //this item stream can read, so the managed archive must stay reachable until this stream is gone.
        readonly SevenZipNativeArchive owner;

        internal NativeItemStream(ItemStreamHandle itemStreamHandle, long length, Action? onDispose, SevenZipNativeArchive owner)
        {
            handle = itemStreamHandle;
            this.length = length;
            this.onDispose = onDispose;
            this.owner = owner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override unsafe int Read(byte[] buffer, int offset, int count)
        {
            //The native side writes `count` bytes at the pinned address, so a count past the buffer's end would
            //corrupt the heap rather than throw - check it here (gh review F32).
            ValidateBufferArguments(buffer, offset, count);
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            if (count == 0) return 0;

            uint processed;
            int hr;
            fixed (byte* p = buffer)
            {
                hr = SevenZip_ItemRead(handle, (IntPtr)(p + offset), (uint)count, out processed);
            }
            GC.KeepAlive(owner);   //its Read/Seek callbacks ran during the native call
            if (hr != 0) throw new IOException($"lib7zNative SevenZip_ItemRead failed: 0x{hr:X8}");

            position += (int)processed;
            return (int)processed;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            uint o = origin switch
            {
                SeekOrigin.Begin => 0u,
                SeekOrigin.Current => 1u,
                SeekOrigin.End => 2u,
                _ => 0u
            };
            int hr = SevenZip_ItemSeek(handle, offset, o, out ulong newPos);
            GC.KeepAlive(owner);
            if (hr != 0) throw new IOException($"lib7zNative SevenZip_ItemSeek failed: 0x{hr:X8}");
            position = (long)newPos;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!handle.IsClosed)
            {
                handle.Dispose();
                onDispose?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
