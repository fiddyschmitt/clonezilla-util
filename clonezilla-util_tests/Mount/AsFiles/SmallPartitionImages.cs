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
    public class SmallPartitionImages
    {
        [TestMethod]
        public void Bzip2()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.bz2" -m L:\""",
                [
                                new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e"),
                ]);
        }

        [TestMethod]
        public void gz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.gz" -m L:\""",
                [
                                new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                ]);

        }

        [TestMethod]
        public void Raw()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img" -m L:\""",
                [
                                new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                ]);
        }

        [TestMethod]
        public void xz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.xz" -m L:\""",
                [
                    new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                ]);
        }

        [TestMethod]
        public void zst()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.zst" -m L:\""",
                [
                    new FileDetails(@"L:\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                ]);
        }
    }
}
