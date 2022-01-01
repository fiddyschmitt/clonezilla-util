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
        public bool LatestReadWasAllNull { get; set; }
        public bool StopReadingWhenRemainderOfFileIsNull { get; set; } = false;

        public SparseAwareReader(Stream stream, bool latestReadWasAllNull)
        {
            Stream = stream;
            LatestReadWasAllNull = latestReadWasAllNull;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var result = Stream.Read(buffer, offset, count);
            return result;
        }
    }
}
