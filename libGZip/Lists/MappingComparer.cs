using libCommon.Lists;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libGZip.Lists
{
    public class MappingComparer : IRangeComparer<Mapping, long>
    {
        public int Compare(Mapping range, long value)
        {
            if (value < range.UncompressedStartByte) return 1;
            if (value > range.UncompressedEndByte) return -1;
            return 0;
        }
    }
}
