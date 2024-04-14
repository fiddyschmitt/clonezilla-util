using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public class PositionTrackerStream : Stream
    {
        public PositionTrackerStream()
        {

        }

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        long length = 0;
        public override long Length => length;

        long position = 0;
        readonly object positionLock = new();
        public override long Position { get => position; set => throw new NotImplementedException(); }

        public override void Flush() => throw new NotImplementedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    lock (positionLock)
                    {
                        position = offset;
                    }
                    break;

                case SeekOrigin.Current:
                    lock (positionLock)
                    {
                        position += offset;
                    }
                    break;

                case SeekOrigin.End:
                    lock (positionLock)
                    {
                        position = Length - offset;
                    }
                    break;
            }

            return position;
        }


        public override void SetLength(long value) => length = value;

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset is out of range.");
            if (count < 0 || (offset + count) > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count), "Count is out of range.");

            lock (positionLock)
            {
                position += count;
                if (position > Length)
                {
                    length = position;
                }
            }
        }

        public override string ToString()
        {
            var result = $"Position: {Position:N0}";
            return result;
        }
    }
}
