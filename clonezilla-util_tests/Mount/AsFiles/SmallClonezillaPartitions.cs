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
    public class SmallClonezillaPartitions
    {
        [TestMethod]
        public void Bzip2()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }

        [TestMethod]
        public void gz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }

        [TestMethod]
        public void Uncompressed()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                    new FileDetails(@"L:\sda2\sda2.txt", "b80328235f5d991c6dc8982e1d1876bc"),
                });
        }

        [TestMethod]
        public void LZ4()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-09-12-20-img_small_drive_using_lz4" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                    new FileDetails(@"L:\sda2\sda2.txt", "b80328235f5d991c6dc8982e1d1876bc"),
                });
        }

        [TestMethod]
        public void LZIP()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-09-12-20-img_small_drive_using_lzip" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                    new FileDetails(@"L:\sda2\sda2.txt", "b80328235f5d991c6dc8982e1d1876bc"),
                });
        }

        [TestMethod]
        public void xz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }

        [TestMethod]
        public void zst()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -m L:\ -p sda1 sdb1""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }
    }
}
