using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static clonezilla_util_tests.Mount.TestUtility;

namespace clonezilla_util_tests.Mount.AsFiles
{
    [TestClass]
    [DoNotParallelize]
    public class LargeDriveImages
    {
        [TestMethod]
        public void bzip2()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.bz2" -m L:\""",
                [
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                ]);
        }

        [TestMethod]
        public void gz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.gz" -m L:\""",
                [
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                ]);
        }

        [TestMethod]
        public void Raw()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img" -m L:\""",
                [
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                ]);
        }

        [TestMethod]
        public void xz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.xz" -m L:\""",
                [
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                ]);
        }

        [TestMethod]
        public void zst()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img.zst" -m L:\""",
                [
                    new FileDetails(@"L:\partition0\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                    new FileDetails(@"L:\partition1\Windows\INF\cpu.inf", "b724a9d3590bab87235fe1de985e748c"),
                ]);
        }
    }
}
