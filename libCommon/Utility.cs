using System;
using System.Collections.Generic;
using System.Management;
using System.Text;
using System.Linq;
using System.IO;

namespace libCommon
{
    public static class Utility
    {
        public static long GetTotalRamSizeBytes()
        {
            var gcMemoryInfo = GC.GetGCMemoryInfo();
            return gcMemoryInfo.TotalAvailableMemoryBytes;
        }

        public static bool IsOnNTFS(string filename)
        {
            //Get all the drives on the local machine.
            var allDrives = DriveInfo.GetDrives();

            //Get the path root.
            var pathRoot = Path.GetPathRoot(filename);
            //Find the drive based on the path root.
            var driveBasedOnPath = allDrives.FirstOrDefault(d => d.RootDirectory.Name.Equals(pathRoot));
            //Determine if NTFS  
            var isNTFS = driveBasedOnPath != null && driveBasedOnPath.DriveFormat == "NTFS";

            return isNTFS;
        }

        public static string Absolutify(string relativePath)
        {
            var result = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            return result;
        }
    }
}
