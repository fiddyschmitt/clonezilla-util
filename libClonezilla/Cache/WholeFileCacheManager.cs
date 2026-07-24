using libCommon;
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

        /// <summary>
        /// Identity folder for a whole (unnamed) stream without reading all of it: MD5 of the first
        /// 50 MB of DECOMPRESSED content, salted with the stream name and compressed length. This is
        /// the exact key the old extraction cache used, so existing index-cache folders stay valid.
        /// </summary>
        public static string GetCacheFolder(Stream decompressedContent, string streamName, long compressedLength)
        {
            if (string.IsNullOrEmpty(RootCacheFolder)) throw new Exception("WholeFileCacheManager has not been initialized with a cache folder.");

            var beginningOfFile = new byte[50 * 1024 * 1024];
            //a single Read() can return fewer bytes than requested (and how many is not deterministic), which would make the cache key vary from run to run
            decompressedContent.ReadAtLeast(beginningOfFile, beginningOfFile.Length, throwOnEndOfStream: false);
            var md5 = libCommon.Utility.CalculateMD5(beginningOfFile);
            md5 = libCommon.Utility.CalculateMD5(Encoding.UTF8.GetBytes($"{md5} {streamName} {compressedLength}"));
            var cacheFolder = Path.Combine(RootCacheFolder, md5);
            Directory.CreateDirectory(cacheFolder);

            return cacheFolder;
        }

        /// <summary>Identity folder for an uncompressed image file: the file serves as its own
        /// "decompressed content" in the key math above.</summary>
        public static string GetCacheFolderForFile(string filename, string streamName)
        {
            using var fs = File.OpenRead(filename);
            return GetCacheFolder(fs, streamName, fs.Length);
        }
    }
}
