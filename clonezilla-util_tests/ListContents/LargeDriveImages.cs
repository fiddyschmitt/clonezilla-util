using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.ListContents
{
    //Sequential: each of these decodes a multi-GB drive image. (list no longer mounts anything, so drive letters are not
    //the reason any more.)
    //Dokan supports mounting to a network drive (eg. \\myfs\\fs1). Perhaps mount to one of these dynamic places so that drive letter collisions can be avoided
    [DoNotParallelize]
    [TestClass]
    public class LargeDriveImages : ListTestBase
    {
        //runaway protection only (~5x headroom): the slowest cold run so far took 50 min (gz, 2026-09-23)
        const int TimeoutMs = 250 * 60_000;

        [TestMethod, Timeout(TimeoutMs)]
        public void Bzip2()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.bz2" """,
                [
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                ]);
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void Gz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.gz""",
                [
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                ]);
        }

        [TestMethod, Timeout(TimeoutMs)]
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

        [TestMethod, Timeout(TimeoutMs)]
        public void Xz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.xz""",
                [
                    @"2021-12-28_pb-devops1_sda.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                    @"2021-12-28_pb-devops1_sda.img\partition1\Windows\INF\cpu.inf"
                ]);
        }

        [TestMethod, Timeout(TimeoutMs)]
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
