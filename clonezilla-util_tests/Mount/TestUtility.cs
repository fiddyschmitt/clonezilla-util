using libCommon;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.Mount
{
    public static class TestUtility
    {
        public static void ConfirmFilesExist(string exeUnderTest, string args, IEnumerable<FileDetails> expectedFiles)
        {
            var psi = new ProcessStartInfo(exeUnderTest, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = Process.Start(psi);

            bool allSuccessful;
            do
            {
                allSuccessful = true;

                if (process?.HasExited ?? true)
                {
                    Debugger.Break();
                }

                foreach (var expectedFile in expectedFiles)
                {
                    bool fileIsAsExpected = false;
                    if (File.Exists(expectedFile.FullPath))
                    {
                        // 13/04/2024: 4 mins
                        //var md5 = libCommon.Utility.CalculateMD5(expectedFile.FullPath);

                        //doesn't support files larger than 2 GB                        
                        //using var ms = new MemoryStream();
                        //using var fs = File.OpenRead(expectedFile.FullPath);
                        //fs.CopyTo(ms, 10 * 1024 * 1024);


                        //Supports larger than 2GB, but caused MD5 checks to fail
                        //using var fs = File.OpenRead(expectedFile.FullPath);
                        //using var memoryMappedFile = MemoryMappedFile.CreateNew(mapName: null, fs.Length);
                        //using var ms = memoryMappedFile.CreateViewStream();
                        //fs.CopyTo(ms, 10 * 1024 * 1024);

                        // 13/04/2024: 40 seconds
                        //todo: Work out why this is faster than just calculating the hash directly on the virtual file
                        using var virtualFile = File.OpenRead(expectedFile.FullPath);
                        var tempFile = File.Create(TempUtility.GetTempFilename(false));
                        virtualFile.CopyTo(tempFile);
                        var md5 = Utility.CalculateMD5(tempFile);

                        var md5Match = md5.Equals(expectedFile.MD5);
                        Assert.IsTrue(md5Match, "MD5 hashes do not match");

                        if (md5Match)
                        {
                            fileIsAsExpected = true;
                        }
                        else
                        {
                            Debugger.Break();
                        }
                    }

                    if (!fileIsAsExpected)
                    {
                        allSuccessful = false;
                        break;
                    }
                };

                if (allSuccessful)
                {
                    break;
                }

                Thread.Sleep(1000);
            } while (!allSuccessful);

            

            process?.Kill();
            process?.WaitForExit();
        }

        public class FileDetails(string fullPath, string md5)
        {
            public string FullPath = fullPath;
            public string MD5 = md5;
        }
    }
}
