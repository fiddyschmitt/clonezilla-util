using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.Tests
{
    public static class ListContentsTests
    {
        public static void Test(string exeUnderTest)
        {
            TestSmallClonezillaPartitions(exeUnderTest);
            TestSmallPartitionImages(exeUnderTest);
            TestDiffertPartcloneFormats(exeUnderTest);            

            TestLargeClonezillaPartitions(exeUnderTest);
            TestLargeDriveImages(exeUnderTest);     
        }

        public static void TestSmallPartitionImages(string exeUnderTest)
        {
            //bzip2
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.bz2""",
                new[] {
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                });

            //gz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.gz""",
                new[] {
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                });

            //raw
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img""",
                new[] {
                    @"2021-12-28_pb-devops1_sda1\partition0\Recovery\WindowsRE\ReAgent.xml",
                });

            //xz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.xz""",
                new[] {
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                });

            //zst
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.zst""",
                new[] {
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                });
        }

        public static void TestLargeDriveImages(string exeUnderTest)
        {
            //bzip2
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.bz2""",
                new[] {
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                });

            //gz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.gz""",
                new[] {
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                });

            //raw
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img""",
                new[] {
                    @"2021-12-28_pb-devops1_sda\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda\partition1\Windows\INF\cpu.inf"
                });

            //xz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.xz""",
                new[] {
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                });

            //zst
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.zst""",
                new[] {
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                });
        }

        public static void TestDiffertPartcloneFormats(string exeUnderTest)
        {
            //two different clonezilla formats
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)" "E:\clonezilla-util-test resources\clonezilla images\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)" """,
                new[] {
                    @"2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda1\sda1.txt",
                    @"2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda2\sda2.txt",

                    @"2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda1\sda1.txt",
                    @"2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda2\sda2.txt"
                });
        }

        public static void TestLargeClonezillaPartitions(string exeUnderTest)
        {
            //bzip2
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2""",
                new[] {
                    @"2022-07-16-22-img_pb-devops1_bzip2\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_bzip2\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-16-22-img_pb-devops1_bzip2\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });

            //gz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz""",
                new[] {
                    @"2022-07-17-16-img_pb-devops1_gz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-16-img_pb-devops1_gz\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-17-16-img_pb-devops1_gz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });

            //xz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz""",
                new[] {
                    @"2022-07-17-12-img_pb-devops1_xz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-12-img_pb-devops1_xz\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-17-12-img_pb-devops1_xz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });

            //zst
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst""",
                new[] {
                    @"2022-07-16-22-img_pb-devops1_zst\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_zst\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-16-22-img_pb-devops1_zst\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });
        }

        public static void TestSmallClonezillaPartitions(string exeUnderTest)
        {
            //bzip2
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2" -p sda1 sdb1""",
                new[] {
                    @"2022-07-16-22-img_pb-devops1_bzip2\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_bzip2\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });

            //gz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -p sda1 sdb1""",
                new[] {
                    @"2022-07-17-16-img_pb-devops1_gz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-16-img_pb-devops1_gz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });

            //xz
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz" -p sda1 sdb1""",
                new[] {
                    @"2022-07-17-12-img_pb-devops1_xz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-12-img_pb-devops1_xz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });

            //zst
            ConfirmContainsStrings(
                exeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -p sda1 sdb1""",
                new[] {
                    @"2022-07-16-22-img_pb-devops1_zst\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_zst\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });
        }

        public static void ConfirmContainsStrings(string exeUnderTest, string args, IEnumerable<string> expectedStrings)
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

            var output = Utility.GetProgramOutput(exeUnderTest, args);

            var success = true;
            expectedStrings
                .ToList()
                .ForEach(expectedString =>
                {
                    var contains = output.Contains(expectedString);
                    if (!contains)
                    {
                        Debugger.Break();
                        success = false;
                    }
                });

            var duration = DateTime.Now - startTime;
            Utility.LogResult(success, args, duration);
        }
    }
}
