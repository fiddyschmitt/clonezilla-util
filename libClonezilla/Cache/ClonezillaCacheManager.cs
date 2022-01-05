using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;

namespace libClonezilla.Cache
{
    public class ClonezillaCacheManager : IClonezillaCacheManager
    {
        public ClonezillaCacheManager(string cacheRootFolder)
        {
            CacheRootFolder = cacheRootFolder;

            TempFolder = Path.Combine(CacheRootFolder, "Temp");
            if (!Directory.Exists(TempFolder))
            {
                Directory.CreateDirectory(TempFolder);
            }
        }

        public string CacheRootFolder { get; }
        public string TempFolder { get; }

        public IPartitionCache GetPartitionCache(string clonezillaFolder, string partitionName)
        {
            string imgIdFilename = Path.Combine(clonezillaFolder, "Info-img-id.txt");
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
