using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public interface ISparseAwareReader
    {
        public Stream Stream { get; }
        public bool LatestReadWasAllNull { get; set; }
        public bool StopReadingWhenRemainderOfFileIsNull { get; set; }
    }
}
