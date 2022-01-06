using System;
using System.Collections.Generic;
using System.Text;

namespace libClonezilla.Cache
{
    public interface IClonezillaCacheManager
    {
        public IPartitionCache GetPartitionCache(string partitionName);
    }
}
