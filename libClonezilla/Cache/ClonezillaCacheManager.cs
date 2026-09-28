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

            string? uniqueIdForClonezillaImage = null;

            if (File.Exists(imgIdFilename))
            {
                //The first 16 characters of IMG_ID name the cache folder. A missing, empty or short IMG_ID line used
                //to throw here (First() / the [..16] slice); it now falls back to the content hash below (gh review F26).
                var imgId = File
                                .ReadAllLines(imgIdFilename)
                                .FirstOrDefault(line => line.StartsWith("IMG_ID="))?["IMG_ID=".Length..]
                                .Trim();

                if (!string.IsNullOrEmpty(imgId))
                {
                    uniqueIdForClonezillaImage = imgId.Length > 16 ? imgId[..16] : imgId;
                }
            }

            if (uniqueIdForClonezillaImage == null)
            {
                //The file we normally use to get a unique id isn't present. Let's calculate a hash based on small files,
                //in a fixed (sorted) order: GetFiles guarantees none, so the id - and every cache keyed by it - could
                //differ between runs or machines (gh review F26).

                var smallFileHashes = Directory
                                        .GetFiles(ClonezillaFolder)
                                        .OrderBy(filename => Path.GetFileName(filename), StringComparer.OrdinalIgnoreCase)
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
