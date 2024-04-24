using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress.Streams
{
    public class SubStream : Stream
    {
        readonly Stream vector;
        private readonly long offset;
        private readonly long length;
        private long position = 0;

        public SubStream(Stream vector, long offset, long length)
        {
            if (length < 1) throw new ArgumentException("Length must be greater than zero.");

            this.vector = vector;
            this.offset = offset;
            this.length = length;

            vector.Seek(offset, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            CheckDisposed();
            long remaining = length - position;
            if (remaining <= 0) return 0;
            if (remaining < count) count = (int)remaining;
            int read = vector.Read(buffer, offset, count);
            position += read;
            return read;
        }

        private void CheckDisposed()
        {
            if (vector == null) throw new ObjectDisposedException(GetType().Name);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long pos = position;

            if (origin == SeekOrigin.Begin)
                pos = offset;
            else if (origin == SeekOrigin.End)
                pos = length + offset;
            else if (origin == SeekOrigin.Current)
                pos += offset;

            if (pos < 0) pos = 0;
            else if (pos >= length) pos = length - 1;

            position = vector.Seek(this.offset + pos, SeekOrigin.Begin) - this.offset;

            return pos;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => position; set { position = this.Seek(value, SeekOrigin.Begin); } }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
}
