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
    /// (the extractor pool) enforces this by checking out one archive per open stream.
    ///
    /// <paramref name="onDispose"/> runs after the native item stream is closed - used to return the
    /// owning worker to the pool.
    /// </summary>
    public sealed class NativeItemStream : Stream
    {
        IntPtr handle;
        readonly long length;
        long position;
        readonly Action? onDispose;

        internal NativeItemStream(IntPtr itemStreamHandle, long length, Action? onDispose)
        {
            handle = itemStreamHandle;
            this.length = length;
            this.onDispose = onDispose;
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
            if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeItemStream));
            if (count <= 0) return 0;

            uint processed;
            int hr;
            fixed (byte* p = &buffer[offset])
            {
                hr = SevenZip_ItemRead(handle, (IntPtr)p, (uint)count, out processed);
            }
            if (hr != 0) throw new IOException($"lib7zNative SevenZip_ItemRead failed: 0x{hr:X8}");

            position += (int)processed;
            return (int)processed;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(NativeItemStream));
            uint o = origin switch
            {
                SeekOrigin.Begin => 0u,
                SeekOrigin.Current => 1u,
                SeekOrigin.End => 2u,
                _ => 0u
            };
            int hr = SevenZip_ItemSeek(handle, offset, o, out ulong newPos);
            if (hr != 0) throw new IOException($"lib7zNative SevenZip_ItemSeek failed: 0x{hr:X8}");
            position = (long)newPos;
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (handle != IntPtr.Zero)
            {
                SevenZip_ItemClose(handle);
                handle = IntPtr.Zero;
                onDispose?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
