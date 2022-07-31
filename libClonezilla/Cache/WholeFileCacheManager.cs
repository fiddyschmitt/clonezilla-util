using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Cache
{
    public static class WholeFileCacheManager
    {
        public static string RootCacheFolder { get; private set; } = "";
        public static void Initialize(string cacheFolder)
        {
            RootCacheFolder = cacheFolder;
            Directory.CreateDirectory(RootCacheFolder);
        }
    }
}
