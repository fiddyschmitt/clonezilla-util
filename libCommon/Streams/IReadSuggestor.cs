using System;
using System.Collections.Generic;
using System.Text;

namespace libCommon.Streams
{
    public interface IReadSuggestor
    {
        (long Start, long End) GetRecommendation(long start);
    }
}
