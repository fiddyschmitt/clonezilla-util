using System;
using System.Collections.Generic;
using System.Diagnostics;
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

            var allSuccessful = true;

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
                        //var md5 = libCommon.Utility.CalculateMD5(expectedFile.FullPath);
                        using var ms = new MemoryStream();
                        using var fs = File.OpenRead(expectedFile.FullPath);
                        fs.CopyTo(ms, 10 * 1024 * 1024);

                        var md5 = libCommon.Utility.CalculateMD5(ms);

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

        public class FileDetails
        {
            public string FullPath;
            public string MD5;

            public FileDetails(string fullPath, string md5)
            {
                FullPath = fullPath;
                MD5 = md5;
            }
        }
    }
}
