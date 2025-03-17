using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static clonezilla_util_tests.Mount.TestUtility;

namespace clonezilla_util_tests.Mount.AsImageFiles
{
    [TestClass]
    [DoNotParallelize]
    public class ImageFileTests
    {
        [TestMethod]
        public void Gz()
        {
            TestUtility.ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -p sda1 -m L:\ """,
                [
                    new FileDetails(@"L:\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                ]);
        }

        [TestMethod]
        public void Partclone()
        {
            //partclone image
            TestUtility.ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\partclone images\2021-12-29 - partclone file\sdb1.ntfs-ptcl-img" -m L:\ """,
                [
                    new FileDetails(@"L:\sdb1.ntfs-ptcl-img.img", "82b4fbf40812fe3354c855ee10fdc839", 1024 * 1024 * 1024),
                ]);
        }

        [TestMethod]
        public void LuksNtfs6GB()
        {
            //6GB ext4 -> luks -> partclone -> zst
            TestUtility.ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-08-16-20-img_luks_test_6GB_ext4_zst\ocs_luks_0Yy.ext4-ptcl-img.zst" --mount L:\ """,
                [
                    new FileDetails(@"L:\partition0.img", "a3217b31c6f2e5270174a9c536a90242"),
                ]);
        }

        [TestMethod]
        public void Zst()
        {
            TestUtility.ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -p sda1 -m L:\ """,
                [
                    new FileDetails(@"L:\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                ]);
        }

        [TestMethod]
        public void UncompressedPartitionImage_and_gzClonezillaImage()
        {
            //uncompressed partition image and gz clonezilla image
            TestUtility.ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img" "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -p sda1 partition0 -m L:\ """,
                [
                    new FileDetails(@"L:\2021-12-28_pb-devops1_sda1\partition0.img", "d4559685d050cb9682369aad7fa0ba24"),
                    new FileDetails(@"L:\2022-07-17-16-img_pb-devops1_gz\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                ]);
        }
    }
}
