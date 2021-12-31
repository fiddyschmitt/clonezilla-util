using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams.Sparse
{
    public interface ISparseAwareWriter
    {
        public Stream Stream { get; }
        public bool ExplicitlyWriteNullBytes { get; set; }
        
    }
}
