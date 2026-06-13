using libCommon.Lists;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDecompression.Lists
{
    public class MappingComparer : IRangeComparer<Mapping, long>
    {
        public int Compare(Mapping range, long value)
        {
            //UncompressedEndByte is exclusive (it equals the next block's UncompressedStartByte)
            if (value < range.UncompressedStartByte) return 1;
            if (value >= range.UncompressedEndByte) return -1;
            return 0;
        }
    }
}
