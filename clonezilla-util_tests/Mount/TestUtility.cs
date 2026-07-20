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
        public static void ConfirmFilesExist(string exeUnderTest, string args, IEnumerable<FileDetails> expectedFiles, TimeSpan? timeout = null)
        {
            var psi = new ProcessStartInfo(exeUnderTest, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = Process.Start(psi);

            //cold index builds for the large drive images legitimately take hours; this only guards
            //against waiting forever (e.g. the exe's mount was destroyed and the files never appear)
            var maxWait = timeout ?? TimeSpan.FromHours(6);
            var waited = Stopwatch.StartNew();

            //the exe must always be killed - even when an assert or an IO error (e.g. the mount
            //vanishing mid-copy) throws - or it lingers, holding GBs and its Dokan mounts (2026-07-17)
            try
            {
                bool allSuccessful;
                do
                {
                    allSuccessful = true;

                    if (process?.HasExited ?? true)
                    {
                        Assert.Fail($"The exe under test exited (code {(process != null ? process.ExitCode.ToString() : "unknown")}) before all expected files were served.");
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
                            using var virtualFile = File.OpenRead(expectedFile.FullPath);
                            using var tempFile = File.Create(TempUtility.GetTempFilename(false), 4096, FileOptions.DeleteOnClose);

                            if (expectedFile.LengthForMd5 == null)
                            {
                                virtualFile.CopyTo(tempFile);
                            }
                            else
                            {
                                virtualFile.CopyTo(tempFile, expectedFile.LengthForMd5.Value, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
                            }
                            var md5 = Utility.CalculateMD5(tempFile);

                            var md5Match = md5.Equals(expectedFile.MD5);
                            Assert.IsTrue(md5Match, $"MD5 mismatch for {expectedFile.FullPath}: expected {expectedFile.MD5}, computed {md5}");

                            fileIsAsExpected = true;
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

                    if (waited.Elapsed > maxWait)
                    {
                        Assert.Fail($"Timed out after {waited.Elapsed} waiting for the expected files to be served. First missing: {expectedFiles.FirstOrDefault(f => !File.Exists(f.FullPath))?.FullPath ?? "(all exist)"}");
                    }

                    Thread.Sleep(1000);
                } while (!allSuccessful);
            }
            finally
            {
                try
                {
                    process?.Kill();
                    process?.WaitForExit();
                }
                catch
                {
                    //the process may already have exited
                }
            }
        }

        public class FileDetails(string fullPath, string md5, long? lengthForMd5 = null)
        {
            public string FullPath = fullPath;
            public string MD5 = md5;
            public long? LengthForMd5 = lengthForMd5;
        }
    }
}
