using libCommon.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams.Sparse
{
    public class SparseAwareReader(Stream stream) : Stream, ISparseAwareReader
    {
        readonly Stream baseStream = stream;

        //must return this wrapper (not the base stream), so that reads through ISparseAwareReader.Stream update LatestReadWasAllNull
        public Stream Stream => this;
        public bool LatestReadWasAllNull { get; set; } = false;
        public bool StopReadingWhenRemainderOfFileIsNull { get; set; } = false;

        public override bool CanRead => baseStream.CanRead;

        public override bool CanSeek => baseStream.CanSeek;

        public override bool CanWrite => baseStream.CanWrite;

        public override long Length => baseStream.Length;

        public override long Position { get => baseStream.Position; set => baseStream.Position = value; }

        public static bool IsAllZeros(byte[] data, int offset, int count)
        {
            var result = data.AsSpan(offset, count).IndexOfAnyExcept((byte)0) < 0;
            return result;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = baseStream.Read(buffer, offset, count);

            //only inspect the bytes actually read; beyond that the buffer holds stale content
            LatestReadWasAllNull = IsAllZeros(buffer, offset, bytesRead);

            return bytesRead;
        }

        public override void Flush()
        {
            baseStream.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var result = baseStream.Seek(offset, origin);
            return result;
        }

        public override void SetLength(long value)
        {
            baseStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            baseStream.Write(buffer, offset, count);
        }
    }
}
