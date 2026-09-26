using System;
using System.Collections.Generic;
using System.Management;
using System.Text;
using System.Linq;
using System.IO;
using System.Security.Cryptography;

namespace libCommon
{
    public static class Utility
    {
        public static bool IsOnNTFS(string filename)
        {
            //Ask the drive holding the path directly. This used to scan every drive for a root name equal to
            //GetPathRoot's, case-sensitively, so "r:\out.img" never matched "R:\" and a relative path matched
            //nothing - extraction then wrote every zero instead of a sparse file (gh review F16).
            try
            {
                var pathRoot = Path.GetPathRoot(Path.GetFullPath(filename));
                if (string.IsNullOrEmpty(pathRoot)) return false;

                var drive = new DriveInfo(pathRoot);   //throws for a UNC root, which we treat as "not NTFS"
                return drive.IsReady && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;   //a probe: when in doubt, write a normal file
            }
        }

        public static string Absolutify(string relativePath)
        {
            var result = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            return result;
        }

        public static string CalculateMD5(string filename)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(filename);
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public static string CalculateMD5(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        public static string CalculateMD5(byte[] bytes)
        {
            var hash = MD5.HashData(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
