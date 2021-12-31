using System;
using System.Collections.Generic;
using System.Text;

namespace libCommon.Streams
{
    public interface IReadSegmentSuggestor
    {
        (long Start, long End) GetRecommendation(long start, long end);
    }
}
