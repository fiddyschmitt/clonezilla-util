using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Lists
{
    public interface IRangeComparer<TRange, TValue>
    {
        /// <summary>
        /// Returns 0 if value is in the specified range;
        /// less than 0 if value is above the range;
        /// greater than 0 if value is below the range.
        /// </summary>
        int Compare(TRange range, TValue value);
    }
}
