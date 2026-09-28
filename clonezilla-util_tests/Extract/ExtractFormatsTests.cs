namespace clonezilla_util_tests.Extract
{
    // extract through every decoder and container shape, with one or two files each. The mount tests pin the same
    // files' MD5s; extract reads them through the same lock-free positional cursors but writes them itself (gh review
    // F7), so a decoder that serves the mount correctly still needs its own extract check.
    [TestClass]
    [DoNotParallelize]
    public class ExtractFormatsTests : ExeTestBase
    {
        const string ClonezillaImages = @"E:\clonezilla-util-test resources\clonezilla images\";
        const string DriveImages = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\";

        const string ReloadXmlMd5 = "f5a6df3c8f1ad69766afee3a25f7e376";
        const string LogoJpgMd5 = "0217ff1926ec5f82e1a120676eff70c3";
        const string ReAgentXmlMd5 = "464bd66c6443e55b791f16cb6bc28c2e";
        const string Sda1Md5 = "c3f38733914d360530455ba3b4073868";
        const string Sda2Md5 = "b80328235f5d991c6dc8982e1d1876bc";

        //one file from each of two partitions of a large Clonezilla image, per codec; -p keeps the 20 GB sda2 out of it
        [TestMethod, Timeout(120 * 60_000)]
        [DataRow("2022-07-16-22-img_pb-devops1_bzip2")]
        [DataRow("2022-07-17-16-img_pb-devops1_gz")]
        [DataRow("2022-07-17-12-img_pb-devops1_xz")]
        [DataRow("2022-07-16-22-img_pb-devops1_zst")]
        public void ClonezillaImage_PerCodec(string image)
        {
            var output = NewTempFolder();
            try
            {
                RunExe(["extract", "--input", ClonezillaImages + image, "-p", "sda1", "sdb1", "-o", output,
                        "--include", $@"{image}\sda1\Recovery\Logs\Reload.xml", $@"{image}\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"])
                    .AssertSucceeded("extract");

                AssertFileMd5(Path.Combine(output, "sda1", "Recovery", "Logs", "Reload.xml"), ReloadXmlMd5);
                AssertFileMd5(Path.Combine(output, "sdb1", "Kingsley", "Prototype 1", "Temp", "Images", "logo.jpg"), LogoJpgMd5);
                Assert.AreEqual(2, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length, "the two specs should match exactly two files");
            }
            finally { TryDeleteFolder(output); }
        }

        //A partition image file, raw and compressed. The compressed ones are decompressed in-process now; they used to be
        //published on a Dokan temp mount and re-read through it, so extract needed Dokan for them (gh review F46).
        [TestMethod, Timeout(60 * 60_000)]
        [DataRow("2021-12-28_pb-devops1_sda1.img")]
        [DataRow("2021-12-28_pb-devops1_sda1.img.bz2")]
        [DataRow("2021-12-28_pb-devops1_sda1.img.gz")]
        [DataRow("2021-12-28_pb-devops1_sda1.img.xz")]
        [DataRow("2021-12-28_pb-devops1_sda1.img.zst")]
        public void PartitionImageFile_PerCodec(string imageFile)
        {
            var output = NewTempFolder();
            try
            {
                RunExe(["extract", "--input", DriveImages + imageFile, "-o", output, "--include", @"partition0\Recovery\WindowsRE\ReAgent.xml"])
                    .AssertSucceeded("extract");

                AssertFileMd5(Path.Combine(output, "partition0", "Recovery", "WindowsRE", "ReAgent.xml"), ReAgentXmlMd5);
            }
            finally { TryDeleteFolder(output); }
        }

        //The small drive (sda1.txt, sda2.txt) as saved by different Clonezilla versions and modes: lz4 and lzip (served
        //through the restart-based legacy cache path), newer partclone releases, and dd mode - whole and split into
        //.aa volumes. The dd images were never exercised before. Their sda1.txt/sda2.txt were checked independently of
        //this tool, from the raw MFT records in each image (2026-09-26).
        [TestMethod, Timeout(30 * 60_000)]
        [DataRow("2022-09-12-20-img_small_drive_using_lz4")]
        [DataRow("2022-09-12-20-img_small_drive_using_lzip")]
        [DataRow("2023-03-29-22-img_small_drive")]
        [DataRow("2023-05-28-19-img_small_drive")]
        [DataRow("2023-11-14-20-img_small_drives_using_clonezilla_dd")]
        [DataRow("2023-11-14-21-img_small_drives_using_clonezilla_dd")]
        public void SmallDrive_PerFormat(string image)
        {
            var output = NewTempFolder();
            try
            {
                RunExe(["extract", "--input", ClonezillaImages + image, "-o", output, "--include", "*.txt"]).AssertSucceeded("extract");

                AssertFileMd5(Path.Combine(output, "sda1", "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(output, "sda2", "sda2.txt"), Sda2Md5);
            }
            finally { TryDeleteFolder(output); }
        }

        //extract-partition-image of a dd-mode image is the raw partition itself. Expected MD5s: the gunzipped
        //sdaN.dd-ptcl-img.gz (identical in the whole and the split image), computed without this tool (2026-09-26).
        [TestMethod, Timeout(30 * 60_000)]
        [DataRow("2023-11-14-20-img_small_drives_using_clonezilla_dd")]
        [DataRow("2023-11-14-21-img_small_drives_using_clonezilla_dd")]
        public void PartitionImage_DdMode(string image)
        {
            var output = NewTempFolder();
            try
            {
                RunExe(["extract-partition-image", "--input", ClonezillaImages + image, "-o", output]).AssertSucceeded("extract-partition-image");

                var sda1 = Path.Combine(output, "sda1.img");
                var sda2 = Path.Combine(output, "sda2.img");
                Assert.AreEqual(20_971_520L, new FileInfo(sda1).Length, "sda1.img has the wrong size");
                Assert.AreEqual(81_788_928L, new FileInfo(sda2).Length, "sda2.img has the wrong size");
                AssertFileMd5(sda1, "8113fa898f510645800bb52393e21f5b");
                AssertFileMd5(sda2, "061afebb33d57892238b058b963a60c4");
            }
            finally { TryDeleteFolder(output); }
        }
    }
}
