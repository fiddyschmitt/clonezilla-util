using System;
using System.IO;

namespace libClonezilla.Extractors
{
    /// <summary>
    /// A seekable, read-only stream over one archive item that borrows a <see cref="NativeExtractorPool"/>
    /// worker only for the duration of each <see cref="Read"/> - never for the lifetime of the open handle.
    ///
    /// This decouples "open file handles" from "native workers": an open handle holds no worker, so opening
    /// a file is instant and unbounded and can't block a Dokan CreateFile callback into a timeout (the
    /// 0x800705AA the old per-handle-checkout model risked once more than poolSize files were open on a
    /// partition). The pool size now caps concurrent *reads* instead. Each read re-opens the item on a
    /// pre-warmed worker and seeks to the current position; workers share the partition's decompression
    /// cache, so the re-seek is cheap.
    ///
    /// Not thread-safe: reads on a single instance must be serialised by the caller (the Dokan layer locks
    /// per handle). Different instances (different handles) are independent and run concurrently.
    /// </summary>
    public sealed class PooledNativeItemStream : Stream
    {
        readonly NativeExtractorPool pool;
        readonly uint index;
        readonly long length;
        long position;
        bool disposed;

        internal PooledNativeItemStream(NativeExtractorPool pool, uint index, long length)
        {
            this.pool = pool;
            this.index = index;
            this.length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => position;
            set => position = value;   //logical only; applied when the next Read borrows a worker
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (count <= 0) return 0;

            var readFrom = position;

            var worker = pool.TakeWorker();   //held only for this read
            var total = 0;
            try
            {
                using var item = worker.OpenItemStream(index, length);
                item.Seek(readFrom, SeekOrigin.Begin);

                //Fill the buffer under this single worker borrow (the native item read can return short),
                //so one ReadFile maps to one worker borrow rather than one borrow per partial read.
                while (total < count)
                {
                    var n = item.Read(buffer, offset + total, count - total);
                    if (n == 0) break;
                    total += n;
                }
            }
            finally
            {
                pool.ReturnWorker(worker);
            }

            position = readFrom + total;
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => length + offset,
                _ => position
            };
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            disposed = true;   //holds no worker, so there is nothing to return to the pool
            base.Dispose(disposing);
        }
    }
}
