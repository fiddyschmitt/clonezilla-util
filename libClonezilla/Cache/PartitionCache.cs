using libClonezilla.Cache.FileSystem;
using System.Linq;
using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using libPartclone;
using libGZip;
using libCommon.Streams;
using System.Collections.ObjectModel;

namespace libClonezilla.Cache
{
    public class PartitionCache : IPartitionCache
    {
        public string ClonezillaCacheFolder { get; }
        public string PartitionName { get; }

        private readonly string partcloneImageInfoFilename;

        public PartitionCache(string clonezillaCacheFolder, string partitionName)
        {
            ClonezillaCacheFolder = clonezillaCacheFolder;
            PartitionName = partitionName;
            partcloneImageInfoFilename = Path.Combine(ClonezillaCacheFolder, partitionName + ".PartcloneContentMapping.json");
        }

        public string GetGztoolIndexFilename()
        {
            string result = Path.Combine(ClonezillaCacheFolder, PartitionName + ".gzi");
            return result;
        }

        public void SetPartcloneImageInfo(PartcloneImageInfo partcloneImageInfo)
        {
            var json = JsonConvert.SerializeObject(partcloneImageInfo, Formatting.Indented);
            File.WriteAllText(partcloneImageInfoFilename, json);
        }
    }
}
