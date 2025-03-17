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
    public class LuksClonezillaImages
    {
        [TestMethod]
        public void luks_ntfs_20GB()
        {
            //20GB ntfs -> luks -> partclone -> zst
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-08-16-15-img_luks_test_20GB_ntfs" --mount L:\ """,
                [
                    new FileDetails(@"L:\howdy.txt", "f521b93b9a4f632a537163f599ded439"),
                    new FileDetails(@"L:\second_file.txt", "b1946ac92492d2347c6235b4d2611184"),
                ]);
        }

        [TestMethod]
        public void luks_ntfs_6GB()
        {
            //6GB ntfs -> luks -> partclone -> zst
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-08-16-20-img_luks_test_6GB_ext4_zst" --mount L:\ """,
                [
                    new FileDetails(@"L:\another_file.txt", "42690a6bf443aa07821ccc51e58e950c"),
                    new FileDetails(@"L:\hello.txt", "ce55c98ac24d4c7764877fa58ab441ef"),
                ]);
        }

        [TestMethod]
        public void luks_ext4_500GB_gz()
        {
            //500 GB ext4 -> luks -> partclone -> gz
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-08-16-10-img_luks_test_500GB_ext4_gz" --mount L:\ """,
                [
                    new FileDetails(@"L:\file1.txt", "bad9425ff652b1bd52b49720abecf0ba"),
                    new FileDetails(@"L:\file2.txt", "0f007fde795734c616b558bc6692c06a"),
                ]);
        }

        [TestMethod]
        public void luks_ext4_500GB_zst()
        {
            //500GB ext4 -> luks -> partclone -> zst
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-08-16-09-img_luks_test_500GB_ext4_zst" --mount L:\ """,
                [
                    new FileDetails(@"L:\file1.txt", "bad9425ff652b1bd52b49720abecf0ba"),
                    new FileDetails(@"L:\file2.txt", "0f007fde795734c616b558bc6692c06a"),
                ]);
        }
    }
}
