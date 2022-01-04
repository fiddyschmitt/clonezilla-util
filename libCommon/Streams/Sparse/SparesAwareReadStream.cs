using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams.Sparse
{
    public class SparseAwareReader : ISparseAwareReader
    {
        public Stream Stream { get; }
        public bool LatestReadWasAllNull { get; set; } = false;
        public bool StopReadingWhenRemainderOfFileIsNull { get; set; } = false;
        public long Length { get; }

        public SparseAwareReader(Stream stream, long length)
        {
            Stream = stream;
            Length = length;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = Stream.Read(buffer, offset, count);

            LatestReadWasAllNull = buffer
                            .Skip(offset)
                            .Take(bytesRead)
                            .All(b => b == 0x0);

            return bytesRead;
        }
    }
}
