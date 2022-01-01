using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams.Sparse
{
    public class SparseAwareWriteStream : ISparseAwareWriter
    {
        public Stream Stream { get; }
        public bool ExplicitlyWriteNullBytes { get; set; } = true;

        public SparseAwareWriteStream(Stream stream, bool explicitlyWriteNullBytes)
        {
            Stream = stream;
            ExplicitlyWriteNullBytes = explicitlyWriteNullBytes;
        }
    }
}
