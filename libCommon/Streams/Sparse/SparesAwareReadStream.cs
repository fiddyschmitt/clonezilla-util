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
        public Stream Stream { get; } = stream;
        public bool LatestReadWasAllNull { get; set; } = false;
        public bool StopReadingWhenRemainderOfFileIsNull { get; set; } = false;

        public override bool CanRead => Stream.CanRead;

        public override bool CanSeek => Stream.CanSeek;

        public override bool CanWrite => Stream.CanWrite;

        public override long Length => Stream.Length;

        public override long Position { get => Stream.Position; set => Stream.Position = value; }

        public static bool IsAllZerosLINQParallel(byte[] data, int offset, int count)
        {
            IEnumerable<byte> dat = data;
            if (offset > 0)
            {
                dat = dat.Skip(offset);
            }

            if (count != data.Length)
            {
                dat = dat.Take(count);
            }

            var result = dat
                            .AsParallel()
                            .WithDegreeOfParallelism(Environment.ProcessorCount)
                            .All(b => b == 0x0);

            return result;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = Stream.Read(buffer, offset, count);

            LatestReadWasAllNull = IsAllZerosLINQParallel(buffer, offset, count);

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
