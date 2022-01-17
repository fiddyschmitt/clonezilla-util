using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;

namespace libClonezilla.Cache
{
    public class ClonezillaCacheManager : IClonezillaCacheManager
    {
        public ClonezillaCacheManager(string clonezillaFolder, string cacheRootFolder)
        {
            ClonezillaFolder = clonezillaFolder;
            CacheRootFolder = cacheRootFolder;
        }

        public string ClonezillaFolder { get; }
        public string CacheRootFolder { get; }

        public IPartitionCache GetPartitionCache(string partitionName)
        {
            string imgIdFilename = Path.Combine(ClonezillaFolder, "Info-img-id.txt");
            string imgId = File.ReadAllLines(imgIdFilename)
                               .First(line => line.StartsWith("IMG_ID="))
                               .Split("=", StringSplitOptions.None)[1][..16];

            string clonezillaCacheFolder = Path.Combine(CacheRootFolder, imgId);
            if (!Directory.Exists(clonezillaCacheFolder))
            {
                Directory.CreateDirectory(clonezillaCacheFolder);
            }
            var result = new PartitionCache(clonezillaCacheFolder, partitionName);
            return result;
        }
    }
}
