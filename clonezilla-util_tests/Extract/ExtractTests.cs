using libCommon;

namespace clonezilla_util_tests.Extract
{
    // Integration tests: run the published exe's `extract` verb and check the files it writes.
    // Extract self-terminates (no mount, no drive letter), so we just WaitForExit and assert.
    [TestClass]
    [DoNotParallelize]
    public class ExtractTests : ExeTestBase
    {
        const string UncompressedSmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";
        const string InputName = "2022-06-27-20-img_small_drive-uncompressed";
        const string Sda1Md5 = "c3f38733914d360530455ba3b4073868";
        const string Sda2Md5 = "b80328235f5d991c6dc8982e1d1876bc";

        //runaway protection only: each of these takes seconds
        const int TimeoutMs = 15 * 60_000;

        void RunExtract(string args, string? workingDirectory = null) => RunExe(args, workingDirectory).AssertSucceeded("extract");

        [TestMethod, Timeout(TimeoutMs)]
        public void DefaultOutput_PreservesLayout()
        {
            // No -o: extract creates <cwd>\<inputName>\<partition>\<path>. Run with cwd = a temp dir.
            var cwd = NewTempFolder();
            try
            {
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" --include *.txt", workingDirectory: cwd);

                var root = Path.Combine(cwd, InputName);
                AssertFileMd5(Path.Combine(root, "sda1", "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(root, "sda2", "sda2.txt"), Sda2Md5);
            }
            finally { TryDeleteFolder(cwd); }
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void Flatten_DropsDirectories()
        {
            var output = NewTempFolder();
            try
            {
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" -o ""{output}"" --include *.txt --flatten");

                AssertFileMd5(Path.Combine(output, "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(output, "sda2.txt"), Sda2Md5);
            }
            finally { TryDeleteFolder(output); }
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void Exclude_RemovesMatch()
        {
            var output = NewTempFolder();
            try
            {
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" -o ""{output}"" --include *.txt --exclude sda2.txt");

                var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
                Assert.AreEqual(1, files.Length, "expected exactly one extracted file");
                Assert.AreEqual("sda1.txt", Path.GetFileName(files[0]));
                Assert.AreEqual(Sda1Md5, Utility.CalculateMD5(files[0]));
            }
            finally { TryDeleteFolder(output); }
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void ListVerbatimSpec_ExtractsExactFile()
        {
            // a line exactly as `list` prints it (container\partition\path) works as an include spec
            var output = NewTempFolder();
            try
            {
                var spec = $@"{InputName}\sda2\sda2.txt";
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" -o ""{output}"" --include ""{spec}""");

                var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
                Assert.AreEqual(1, files.Length, "expected exactly the one file named by the list line");
                Assert.AreEqual("sda2.txt", Path.GetFileName(files[0]));
                Assert.AreEqual(Sda2Md5, Utility.CalculateMD5(files[0]));
            }
            finally { TryDeleteFolder(output); }
        }

        //Extracted files carry the archive's timestamps. The expected values are the NTFS $STANDARD_INFORMATION times of
        //sda1.txt, read straight from its MFT record (2019-11-27 20:12:47.7608397 UTC modified).
        [TestMethod, Timeout(TimeoutMs)]
        public void Timestamps_AreRestored()
        {
            var output = NewTempFolder();
            try
            {
                RunExe(["extract", "--input", UncompressedSmallDrive, "-o", output, "--include", "sda1.txt"]).AssertSucceeded("extract");

                var file = Path.Combine(output, "sda1", "sda1.txt");
                AssertFileMd5(file, Sda1Md5);
                AssertClose(new DateTime(2019, 11, 27, 20, 12, 47, 760, DateTimeKind.Utc).AddTicks(8397), File.GetLastWriteTimeUtc(file), "last write time");
                AssertClose(new DateTime(2019, 11, 27, 20, 12, 47, 760, DateTimeKind.Utc).AddTicks(6242), File.GetCreationTimeUtc(file), "creation time");
            }
            finally { TryDeleteFolder(output); }
        }

        static void AssertClose(DateTime expected, DateTime actual, string what) =>
            Assert.IsTrue((actual - expected).Duration() < TimeSpan.FromSeconds(1), $"{what}: expected {expected:O}, got {actual:O}");

        //A trailing separator on --input once gave the container an empty name, so a list-verbatim spec (which starts with
        //the container name) matched nothing (gh review F10). Both separators, as a user might type them.
        [TestMethod, Timeout(TimeoutMs)]
        [DataRow(UncompressedSmallDrive + @"\")]
        [DataRow(@"E:/clonezilla-util-test resources/clonezilla images/2022-06-27-20-img_small_drive-uncompressed/")]
        public void TrailingSeparatorInput_KeepsContainerName(string input)
        {
            var output = NewTempFolder();
            try
            {
                RunExe(["extract", "--input", input, "-o", output, "--include", $@"{InputName}\sda2\sda2.txt"]).AssertSucceeded("extract");

                var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
                Assert.AreEqual(1, files.Length, "the list-verbatim spec should match exactly sda2.txt");
                AssertFileMd5(Path.Combine(output, "sda2", "sda2.txt"), Sda2Md5);
            }
            finally { TryDeleteFolder(output); }
        }

        //A relative --input, resolved against the working directory. Run from a throwaway copy of the image: the exe writes
        //its log under its working directory, which must never be the test assets.
        [TestMethod, Timeout(TimeoutMs)]
        [DataRow(InputName)]
        [DataRow(@".\" + InputName + @"\")]
        public void RelativeInput(string input)
        {
            var work = NewTempFolder();
            try
            {
                CopyFolderFiles(UncompressedSmallDrive, Path.Combine(work, InputName));
                var output = Path.Combine(work, "out");

                RunExe(["extract", "--input", input, "-o", output, "--include", $@"{InputName}\sda1\sda1.txt", "--cache-folder", Path.Combine(work, "cache")], workingDirectory: work)
                    .AssertSucceeded("extract");

                AssertFileMd5(Path.Combine(output, "sda1", "sda1.txt"), Sda1Md5);
            }
            finally { TryDeleteFolder(work); }
        }

        //No -o with a relative input: the default output folder is named after the input, and since that name IS the
        //input folder here, it gets the "-extracted" suffix instead of writing into the image (gh review F58).
        [TestMethod, Timeout(TimeoutMs)]
        public void RelativeInput_DefaultOutputIsBesideTheInput()
        {
            var work = NewTempFolder();
            try
            {
                CopyFolderFiles(UncompressedSmallDrive, Path.Combine(work, InputName));

                RunExe(["extract", "--input", InputName, "--include", "*.txt", "--cache-folder", Path.Combine(work, "cache")], workingDirectory: work)
                    .AssertSucceeded("extract");

                var root = Path.Combine(work, InputName + "-extracted");
                AssertFileMd5(Path.Combine(root, "sda1", "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(root, "sda2", "sda2.txt"), Sda2Md5);
                Assert.IsFalse(Directory.Exists(Path.Combine(work, InputName, "sda1")), "extract wrote into the input image folder");
            }
            finally { TryDeleteFolder(work); }
        }

        //No -o with a compressed image FILE: every image and compression extension is peeled from the default folder
        //name - sda1.img.zst gives "..._sda1", where stripping only the last one gave "..._sda1.img" (gh review F58).
        [TestMethod, Timeout(60 * 60_000)]
        public void CompressedImageFile_DefaultOutputName()
        {
            var cwd = NewTempFolder();
            try
            {
                RunExe(["extract", "--input", @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.zst",
                        "--include", @"partition0\Recovery\WindowsRE\ReAgent.xml"], workingDirectory: cwd)
                    .AssertSucceeded("extract");

                AssertFileMd5(Path.Combine(cwd, "2021-12-28_pb-devops1_sda1", "partition0", "Recovery", "WindowsRE", "ReAgent.xml"), "464bd66c6443e55b791f16cb6bc28c2e");
            }
            finally { TryDeleteFolder(cwd); }
        }
    }
}
