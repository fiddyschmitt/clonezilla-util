﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.ListContents
{
    //Can't parallelize yet, because ListContents mounts an available drive letter which can collide with other tests.
    //Dokan supports mounting to a network drive (eg. \\myfs\\fs1). Perhaps mount to one of these dynamic places so that drive letter collisions can be avoided
    [DoNotParallelize]
    [TestClass]
    public class LargeDriveImages
    {
        [TestMethod]
        public void Bzip2()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.bz2" """,
                [
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                ]);
        }

        [TestMethod]
        public void Gz()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.gz""",
                [
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                ]);
        }

        [TestMethod]
        public void Raw()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img""",
                [
                    @"2021-12-28_pb-devops1_sda\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda\partition1\Windows\INF\cpu.inf"
                ]);
        }

        [TestMethod]
        public void Xz()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.xz""",
                [
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                ]);
        }

        [TestMethod]
        public void Zst()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.zst""",
                [
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                ]);
        }
    }
}
