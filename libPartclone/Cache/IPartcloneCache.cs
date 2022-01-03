using System;
using System.Collections.Generic;
using System.Text;

namespace libPartclone.Cache
{
    public interface IPartcloneCache
    {
        //public PartcloneImageInfo? GetPartcloneImageInfo();
        public List<ContiguousRange>? GetPartcloneContentMapping();
        //public void SetPartcloneImageInfo(PartcloneImageInfo partcloneImageInfo);
        public void SetPartcloneContentMapping(List<ContiguousRange> contiguousRanges);
    }
}
