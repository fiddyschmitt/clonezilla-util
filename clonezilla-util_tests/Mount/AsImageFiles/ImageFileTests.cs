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
        public void Bzip2()
        {
            TestUtility.ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2" -p sda1 -m L:\ """,
                [
                    new FileDetails(@"L:\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                ]);
        }

        [TestMethod]
        public void Xz()
        {
            TestUtility.ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz" -p sda1 -m L:\ """,
                [
                    new FileDetails(@"L:\sda1.img", "d9a64d07801cb9f5878a72da4c5d53fd"),
                ]);
        }

        //the .vhdx is for BitLocker partitions only - 7-Zip opens the rest from the .img - and nothing tells the user to
        //attach one
        [TestMethod]
        public void NoVirtualDisks_ForAnUnencryptedPartition()
        {
            TestUtility.WhileMounted(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -p sda1 -m L:\ """,
                @"L:\",
                mount =>
                {
                    WaitForFile(@"L:\sda1.img");
                    Assert.IsFalse(File.Exists(@"L:\sda1.vhdx"), "no .vhdx for an NTFS partition");
                    Assert.IsFalse(File.Exists(@"L:\sda1.vhd"), "no .vhd for an NTFS partition");

                    //printed after any attach command, so by then there would have been one
                    mount.WaitForOutput("Running. Press Enter to exit.", TimeSpan.FromSeconds(60));
                    Assert.IsFalse(mount.Output.Any(line => line.Contains("Mount-DiskImage")), "no attach command for an NTFS partition");
                });
        }

        //sda3 is a BitLocker volume (Windows 11, saved encrypted). Beside its .img it is served as sda3.vhdx - a GPT disk
        //holding the partition, which Windows attaches and then offers to unlock - and the exe prints the command that
        //attaches it. Pins the disk's first 64 MiB (the VHDX structures, the GPT, the volume's start) rather than reading
        //all 56 GiB. No .vhd: FixedVhd is kept but not served.
        [TestMethod]
        public void BitLocker_ServedAsVhdx()
        {
            TestUtility.WhileMounted(
                Main.ExeUnderTest,
                """mount-as-image-files --input "E:\clonezilla-util-test resources\clonezilla images\2026-10-05-22-img_win11_bitlocker_zst" -p sda3 -m L:\ """,
                @"L:\",
                mount =>
                {
                    WaitForFile(@"L:\sda3.vhdx");
                    Assert.AreEqual("b4bf12b89ecc8c12e3a656bc0ce15693", Md5OfStart(@"L:\sda3.vhdx", 64 * 1024 * 1024), "sda3.vhdx");
                    Assert.IsTrue(File.GetAttributes(@"L:\sda3.vhdx").HasFlag(FileAttributes.ReadOnly), "served read-only");
                    Assert.IsFalse(File.Exists(@"L:\sda3.vhd"), "no .vhd");

                    mount.WaitForOutput("Running. Press Enter to exit.", TimeSpan.FromSeconds(60));
                    Assert.IsTrue(mount.Output.Any(line => line.Contains("Mount-DiskImage -Access ReadOnly -ImagePath \"L:\\sda3.vhdx\"")),
                        $"the attach command. Output:{Environment.NewLine}{string.Join(Environment.NewLine, mount.Output)}");
                },
                TimeSpan.FromMinutes(50));
        }

        //the files appear once the exe reports "Mounting complete" - give Dokan a moment to expose them
        static void WaitForFile(string path)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(path))
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(60)) Assert.Fail($"{path} did not appear within 60 s of 'Mounting complete'.");
                Thread.Sleep(500);
            }
        }

        static string Md5OfStart(string path, int length)
        {
            using var file = File.OpenRead(path);
            var buffer = new byte[length];
            file.ReadExactly(buffer);
            return Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(buffer));
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
