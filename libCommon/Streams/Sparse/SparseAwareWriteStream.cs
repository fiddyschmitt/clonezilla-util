using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams.Sparse
{
    public class SparseAwareWriteStream(Stream stream, bool explicitlyWriteNullBytes) : ISparseAwareWriter
    {
        public Stream Stream { get; } = stream;
        public bool ExplicitlyWriteNullBytes { get; set; } = explicitlyWriteNullBytes;
    }
}
