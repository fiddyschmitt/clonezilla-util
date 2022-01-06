using System;
using System.Collections.Generic;
using System.Text;

namespace libPartclone.Cache
{
    public interface IPartcloneCache
    {
        public List<ContiguousRange>? GetPartcloneContentMapping();
        public void SetPartcloneContentMapping(List<ContiguousRange> contiguousRanges);
    }
}
