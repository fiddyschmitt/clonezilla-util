using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.Tests
{
    public static class MountTests
    {
        public static void Test(string exeUnderTest)
        {
            TestMisc(exeUnderTest);
            //TestDifferentPartcloneVersions(exeUnderTest);

            //TestSmallClonezillaPartitions(exeUnderTest);
            //TestSmallPartitionImages(exeUnderTest);

            //TestLargeClonezillaPartitions(exeUnderTest);
            //TestLargeDriveImages(exeUnderTest);
        }

        public static void TestMisc(string exeUnderTest)
        {
            //multiple containers, multiple partitions
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.zst" -p sda1 sdb1 partition0 --mount L:\ """,
                new[] {
                    new FileDetails(@"L:\2022-07-17-16-img_pb-devops1_gz\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\2022-07-17-16-img_pb-devops1_gz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),

                    new FileDetails(@"L:\2021-12-28_pb-devops1_sda1.img\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                });

            //partclone image
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\partclone images\2021-12-29 - partclone file\sdb1.ntfs-ptcl-img" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });

            //latest clonezilla 29/06/2022
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_smalldrive" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                });
        }

        public static void TestDifferentPartcloneVersions(string exeUnderTest)
        {
            //two different clonezilla formats
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)" "E:\clonezilla-util-test resources\clonezilla images\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                    new FileDetails(@"L:\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda2\sda2.txt", "b80328235f5d991c6dc8982e1d1876bc"),

                    new FileDetails(@"L:\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                    new FileDetails(@"L:\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda2\sda2.txt", "b80328235f5d991c6dc8982e1d1876bc"),
                });

            //latest clonezilla 29/06/2022
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_smalldrive" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                });
        }

        public static void TestSmallPartitionImages(string exeUnderTest)
        {
            //bzip2
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.bz2" -m L:\""",
                new[] {
                    new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                });

            //gz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.gz" -m L:\""",
                new[] {
                    new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                });

            //raw
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img" -m L:\""",
                new[] {
                    new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                });

            //xz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.xz" -m L:\""",
                new[] {
                    new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                });

            //zst
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.zst" -m L:\""",
                new[] {
                    new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                });
        }

        public static void TestLargeDriveImages(string exeUnderTest)
        {
            //bzip2
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.bz2" -m L:\""",
                new[] {
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                });

            //gz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.gz" -m L:\""",
                new[] {
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                });

            //raw
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img" -m L:\""",
                new[] {
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                });

            //xz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.xz" -m L:\""",
                new[] {
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                });

            //zst
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.zst" -m L:\""",
                new[] {
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                });
        }

        public static void TestLargeClonezillaPartitions(string exeUnderTest)
        {
            //bzip2
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });

            //gz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });

            //xz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });

            //zst
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }

        public static void TestSmallClonezillaPartitions(string exeUnderTest)
        {
            //bzip2
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });

            //gz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });

            //uncompressed
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_smalldrive-uncompressed" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                });

            //xz
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });

            //zst
            ConfirmFilesExist(
                exeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }

        public static void ConfirmFilesExist(string exeUnderTest, string args, IEnumerable<FileDetails> expectedFiles)
        {
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

                if (process?.HasExited ?? true)
                {
                    Debugger.Break();
                }

                foreach (var expectedFile in expectedFiles)
                {
                    bool fileIsAsExpected = false;
                    if (File.Exists(expectedFile.FullPath))
                    {
                        var md5 = libCommon.Utility.CalculateMD5(expectedFile.FullPath);

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

            process?.Kill();
            process?.WaitForExit();

            if (allSuccessful)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"Success");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"Fail");
            }

            Console.ResetColor();
            Console.WriteLine($": {args}");
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
