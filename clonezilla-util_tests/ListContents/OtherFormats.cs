namespace clonezilla_util_tests.ListContents
{
    // list over the formats the mount tests serve but no list test covered: lz4 and lzip (restart-based legacy cache path),
    // LUKS, ext4 (with and without LVM) and bare ext4 images, newer partclone releases, and dd mode (whole and split into
    // .aa volumes). Paths are the files the mount tests pin, as list prints them: container\partition\path.
    [TestClass]
    [DoNotParallelize]
    public class OtherFormats : ListTestBase
    {
        const string ClonezillaImages = @"E:\clonezilla-util-test resources\clonezilla images\";

        //runaway protection only: the ext4 roots hold ~214k entries each; everything else here is small
        const int SmallTimeoutMs = 15 * 60_000;
        const int Ext4TimeoutMs = 60 * 60_000;

        static void ConfirmListed(string input, params string[] expected) =>
            TestUtility.ConfirmContainsStrings(Main.ExeUnderTest, $@"list --input ""{input}""", expected);

        [TestMethod, Timeout(SmallTimeoutMs)]
        [DataRow("2022-09-12-20-img_small_drive_using_lz4")]
        [DataRow("2022-09-12-20-img_small_drive_using_lzip")]
        [DataRow("2023-03-29-22-img_small_drive")]
        [DataRow("2023-05-28-19-img_small_drive")]
        [DataRow("2023-11-14-20-img_small_drives_using_clonezilla_dd")]
        [DataRow("2023-11-14-21-img_small_drives_using_clonezilla_dd")]
        public void SmallDrive(string image) =>
            ConfirmListed(ClonezillaImages + image, $@"{image}\sda1\sda1.txt", $@"{image}\sda2\sda2.txt");

        [TestMethod, Timeout(Ext4TimeoutMs)]
        public void Luks_Ntfs20GB() =>
            ConfirmListed(ClonezillaImages + "2022-08-16-15-img_luks_test_20GB_ntfs",
                @"2022-08-16-15-img_luks_test_20GB_ntfs\ocs_luks_esc\howdy.txt",
                @"2022-08-16-15-img_luks_test_20GB_ntfs\ocs_luks_esc\second_file.txt");

        [TestMethod, Timeout(Ext4TimeoutMs)]
        public void Luks_Ext4_6GB() =>
            ConfirmListed(ClonezillaImages + "2022-08-16-20-img_luks_test_6GB_ext4_zst",
                @"2022-08-16-20-img_luks_test_6GB_ext4_zst\ocs_luks_0Yy\another_file.txt",
                @"2022-08-16-20-img_luks_test_6GB_ext4_zst\ocs_luks_0Yy\hello.txt");

        [TestMethod, Timeout(Ext4TimeoutMs)]
        public void Ubuntu_Ext4() =>
            ConfirmListed(ClonezillaImages + "2022-09-13-12-img_ubuntu_22.04_default_filesystem",
                @"2022-09-13-12-img_ubuntu_22.04_default_filesystem\sda2\EFI\ubuntu\grub.cfg",
                @"2022-09-13-12-img_ubuntu_22.04_default_filesystem\sda3\etc\fstab");

        [TestMethod, Timeout(Ext4TimeoutMs)]
        public void Ubuntu_Ext4OnLvm() =>
            ConfirmListed(ClonezillaImages + "2022-09-13-12-img_ubuntu_22.04_lvm_filesystem",
                @"2022-09-13-12-img_ubuntu_22.04_lvm_filesystem\sda2\EFI\ubuntu\grub.cfg",
                @"2022-09-13-12-img_ubuntu_22.04_lvm_filesystem\vgubuntu-root\etc\fstab");

        //a bare ext4 filesystem image: one partition, named partition0; the compressed one keeps ".img" in its container name
        [TestMethod, Timeout(SmallTimeoutMs)]
        [DataRow("2022-08-16_ext4.img", "2022-08-16_ext4")]
        [DataRow("2022-08-16_ext4.img.zst", "2022-08-16_ext4.img")]
        public void Ext4Image(string file, string container) =>
            ConfirmListed(@"E:\clonezilla-util-test resources\drive images\ext4\" + file,
                $@"{container}\partition0\hello.txt",
                $@"{container}\partition0\config_files\test.xml");
    }
}
