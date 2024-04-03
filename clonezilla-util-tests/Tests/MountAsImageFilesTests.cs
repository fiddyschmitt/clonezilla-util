using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.Tests
{
    public static class MountAsImageFilesTests
    {
        public static void Test(string exeUnderTest)
        {
            //gz
            ConfirmFilesExist(
                exeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -p sda1 -m L:\ """,
                new[] {
                    new FileDetails(@"L:\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                });

            //partclone image. This is 2TB - takes ages to calculate the hash
            /*
            ConfirmFilesExist(
                exeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\partclone images\2021-12-29 - partclone file\sdb1.ntfs-ptcl-img" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\sdb1.ntfs-ptcl-img.img", "abc"),
                });
            */

            //6GB ext4 -> luks -> partclone -> zst
            //The stream is too longer for some reason
            /*
            ConfirmFilesExist(
                exeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-08-16-20-img_luks_test_6GB_ext4_zst\ocs_luks_0Yy.ext4-ptcl-img.zst" --mount L:\ """,
                new[] {
                    new FileDetails(@"L:\partition0.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                });
            */

            //zst
            ConfirmFilesExist(
                exeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -p sda1 -m L:\ """,
                new[] {
                    new FileDetails(@"L:\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                });

            //uncompressed partition image and gz clonezilla image
            ConfirmFilesExist(
                exeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img" "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -p sda1 partition0 -m L:\ """,
                new[] {
                    new FileDetails(@"L:\2021-12-28_pb-devops1_sda1\partition0.img", "d4559685d050cb9682369aad7fa0ba24"),
                    new FileDetails(@"L:\2022-07-17-16-img_pb-devops1_gz\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                });
        }

        public static void ConfirmFilesExist(string exeUnderTest, string args, IEnumerable<FileDetails> expectedFiles)
        {
            var startTime = DateTime.Now;

            Process
                .GetProcesses()
                .Where(pr => pr.ProcessName == "clonezilla-util")
                .ToList()
                .ForEach(p =>
                {
                    p.Kill();
                    p.WaitForExit();
                });

            var psi = new ProcessStartInfo(exeUnderTest, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            var process = Process.Start(psi);

            var allSuccessful = true;

            do
            {
                allSuccessful = true;

                foreach (var expectedFile in expectedFiles)
                {
                    bool fileIsAsExpected = false;
                    if (File.Exists(expectedFile.FullPath))
                    {
                        //var md5 = libCommon.Utility.CalculateMD5(expectedFile.FullPath);
                        using var ms = new MemoryStream();
                        using var fs = File.OpenRead(expectedFile.FullPath);
                        fs.CopyTo(ms, 10 * 1024 * 1024);
                        ms.Seek(0, SeekOrigin.Begin);
                        var md5 = libCommon.Utility.CalculateMD5(ms);


                        if (md5.Equals(expectedFile.MD5))
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

            var duration = DateTime.Now - startTime;
            Utility.LogResult(allSuccessful, args, duration);
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
