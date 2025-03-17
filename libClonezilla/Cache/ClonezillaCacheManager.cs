using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using libCommon;

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

            string? uniqueIdForClonezillaImage;

            if (File.Exists(imgIdFilename))
            {
                uniqueIdForClonezillaImage = File
                                                .ReadAllLines(imgIdFilename)
                                                .First(line => line.StartsWith("IMG_ID="))
                                                .Split("=", StringSplitOptions.None)[1][..16];
            }
            else
            {
                //The file we normally use to get a unique id isn't present. Let's calculate a hash based on small files.

                var smallFileHashes = Directory
                                        .GetFiles(ClonezillaFolder)
                                        .Where(filename => new FileInfo(filename).Length < 1024 * 1024)
                                        .Take(100)
                                        .Select(filename => libCommon.Utility.CalculateMD5(filename))
                                        .ToString(Environment.NewLine);
                var smallFileHashesBytes = Encoding.UTF8.GetBytes(smallFileHashes);

                uniqueIdForClonezillaImage = libCommon.Utility.CalculateMD5(smallFileHashesBytes);
            }

            var clonezillaCacheFolder = Path.Combine(CacheRootFolder, uniqueIdForClonezillaImage);
            if (!Directory.Exists(clonezillaCacheFolder))
            {
                Directory.CreateDirectory(clonezillaCacheFolder);
            }

            var result = new PartitionCache(clonezillaCacheFolder, partitionName);
            return result;
        }
    }
}
