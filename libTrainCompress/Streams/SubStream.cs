using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress.Streams
{
    public class SubStream : Stream
    {
        Stream Vector;
        long Offset, _Length, _Position = 0;
        public SubStream(Stream vector, long offset, long length)
        {
            if (length < 1) throw new ArgumentException("Length must be greater than zero.");

            this.Vector = vector;
            this.Offset = offset;
            this._Length = length;

            vector.Seek(offset, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            CheckDisposed();
            long remaining = _Length - _Position;
            if (remaining <= 0) return 0;
            if (remaining < count) count = (int)remaining;
            int read = Vector.Read(buffer, offset, count);
            _Position += read;
            return read;
        }

        private void CheckDisposed()
        {
            if (Vector == null) throw new ObjectDisposedException(GetType().Name);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long pos = _Position;

            if (origin == SeekOrigin.Begin)
                pos = offset;
            else if (origin == SeekOrigin.End)
                pos = _Length + offset;
            else if (origin == SeekOrigin.Current)
                pos += offset;

            if (pos < 0) pos = 0;
            else if (pos >= _Length) pos = _Length - 1;

            _Position = Vector.Seek(this.Offset + pos, SeekOrigin.Begin) - this.Offset;

            return pos;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _Length;

        public override long Position { get => _Position; set { _Position = this.Seek(value, SeekOrigin.Begin); } }

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
