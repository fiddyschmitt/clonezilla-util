using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon
{
    public static class TempUtility
    {
        static string tempRoot = Path.GetTempPath();
        public static string TempRoot
        {
            get
            {
                return tempRoot;
            }

            set
            {
                tempRoot = value;
                if (!Directory.Exists(tempRoot))
                {
                    Directory.CreateDirectory(tempRoot);
                }
            }
        }

        static readonly List<string> Folders = new();
        static readonly List<string> Files = new();

        public static string GetTemporaryDirectory()
        {
            string tempDirectory = Path.Combine(TempRoot, Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
            Directory.CreateDirectory(tempDirectory);

            lock (Folders)
            {
                Folders.Add(tempDirectory);
            }

            return tempDirectory;
        }

        public static string GetTempFilename(bool createEmptyFile)
        {
            var tempFilename = Path.Combine(TempRoot, Path.GetRandomFileName());

            if (createEmptyFile)
            {
                File.Create(tempFilename).Close();
                File.SetAttributes(tempFilename, FileAttributes.Temporary);
            }

            lock (Files)
            {
                Files.Add(tempFilename);
            }

            return tempFilename;
        }

        public static void Cleanup()
        {
            lock (Folders)
            {
                Folders
                    .ForEach(folder =>
                    {
                        try
                        {
                            Directory.Delete(folder, true);
                        }
                        catch { }
                    });
            }

            lock (Files)
            {
                Files
                    .ForEach(filename =>
                    {
                        try
                        {
                            File.Delete(filename);
                        }
                        catch { }
                    });
            }
        }
    }
}
