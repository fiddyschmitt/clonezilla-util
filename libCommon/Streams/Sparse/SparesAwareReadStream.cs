using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams.Sparse
{
    public class SparseAwareReader : Stream, ISparseAwareReader
    {
        public Stream Stream { get; }
        public bool LatestReadWasAllNull { get; set; } = false;
        public bool StopReadingWhenRemainderOfFileIsNull { get; set; } = false;

        public override bool CanRead => Stream.CanRead;

        public override bool CanSeek => Stream.CanSeek;

        public override bool CanWrite => Stream.CanWrite;

        public override long Length => Stream.Length;

        public override long Position { get => Stream.Position; set => Stream.Position = value; }

        public SparseAwareReader(Stream stream)
        {
            Stream = stream;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = Stream.Read(buffer, offset, count);

            LatestReadWasAllNull = buffer
                            .Skip(offset)
                            .Take(bytesRead)
                            .All(b => b == 0x0);

            return bytesRead;
        }

        public override void Flush()
        {
            Stream.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var result = Stream.Seek(offset, origin);
            return result;
        }

        public override void SetLength(long value)
        {
            Stream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Stream.Write(buffer, offset, count);
        }
    }
}
