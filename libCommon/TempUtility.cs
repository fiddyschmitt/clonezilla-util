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
        static List<string> Folders = new List<string>();
        static List<string> Files = new List<string>();

        public static string GetTemporaryDirectory()
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
            Directory.CreateDirectory(tempDirectory);

            lock (Folders)
            {
                Folders.Add(tempDirectory);
            }

            return tempDirectory;
        }

        public static string GetTempFilename(bool createEmptyFile)
        {
            var tempFilename = Path.GetTempFileName();

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
