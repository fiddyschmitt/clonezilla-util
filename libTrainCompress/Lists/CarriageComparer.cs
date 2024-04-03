using libCommon.Lists;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress.Lists
{
    public class CarriageComparer : IRangeComparer<Carriage, long>
    {
        public int Compare(Carriage range, long value)
        {
            if (value < range.UncompressedStartByte) return 1;
            if (value >= range.UncompressedEndByte) return -1;
            return 0;
        }
    }
}
