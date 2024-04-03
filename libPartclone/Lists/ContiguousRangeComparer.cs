using libCommon.Lists;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone.Lists
{
    public class ContiguousRangeComparer : IRangeComparer<ContiguousRange, long>
    {
        public int Compare(ContiguousRange range, long value)
        {
            if (value < range.OutputFileRange.StartByte) return 1;
            if (value > range.OutputFileRange.EndByte) return -1;
            return 0;
        }
    }
}
