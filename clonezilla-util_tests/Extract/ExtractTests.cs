using System.Diagnostics;
using libCommon;

namespace clonezilla_util_tests.Extract
{
    // Integration tests: run the published exe's `extract` verb and check the files it writes.
    // Extract self-terminates (no mount, no drive letter), so we just WaitForExit and assert.
    [TestClass]
    [DoNotParallelize]
    public class ExtractTests
    {
        const string UncompressedSmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";
        const string InputName = "2022-06-27-20-img_small_drive-uncompressed";
        const string Sda1Md5 = "c3f38733914d360530455ba3b4073868";
        const string Sda2Md5 = "b80328235f5d991c6dc8982e1d1876bc";

        static void RunExtract(string args, string? workingDirectory = null)
        {
            var psi = new ProcessStartInfo(Main.ExeUnderTest, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;

            var process = Process.Start(psi);
            Assert.IsNotNull(process);
            process!.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, "extract exited with a non-zero code");
        }

        [TestMethod]
        public void DefaultOutput_PreservesLayout()
        {
            // No -o: extract creates <cwd>\<inputName>\<partition>\<path>. Run with cwd = a temp dir.
            var cwd = Directory.CreateTempSubdirectory().FullName;
            try
            {
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" --include *.txt", workingDirectory: cwd);

                var root = Path.Combine(cwd, InputName);
                var sda1 = Path.Combine(root, "sda1", "sda1.txt");
                var sda2 = Path.Combine(root, "sda2", "sda2.txt");
                Assert.IsTrue(File.Exists(sda1), $"missing {sda1}");
                Assert.IsTrue(File.Exists(sda2), $"missing {sda2}");
                Assert.AreEqual(Sda1Md5, Utility.CalculateMD5(sda1));
                Assert.AreEqual(Sda2Md5, Utility.CalculateMD5(sda2));
            }
            finally { Directory.Delete(cwd, true); }
        }

        [TestMethod]
        public void Flatten_DropsDirectories()
        {
            var output = Directory.CreateTempSubdirectory().FullName;
            try
            {
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" -o ""{output}"" --include *.txt --flatten");

                var sda1 = Path.Combine(output, "sda1.txt");
                var sda2 = Path.Combine(output, "sda2.txt");
                Assert.IsTrue(File.Exists(sda1), $"missing {sda1}");
                Assert.IsTrue(File.Exists(sda2), $"missing {sda2}");
                Assert.AreEqual(Sda1Md5, Utility.CalculateMD5(sda1));
                Assert.AreEqual(Sda2Md5, Utility.CalculateMD5(sda2));
            }
            finally { Directory.Delete(output, true); }
        }

        [TestMethod]
        public void Exclude_RemovesMatch()
        {
            var output = Directory.CreateTempSubdirectory().FullName;
            try
            {
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" -o ""{output}"" --include *.txt --exclude sda2.txt");

                var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
                Assert.AreEqual(1, files.Length, "expected exactly one extracted file");
                Assert.AreEqual("sda1.txt", Path.GetFileName(files[0]));
                Assert.AreEqual(Sda1Md5, Utility.CalculateMD5(files[0]));
            }
            finally { Directory.Delete(output, true); }
        }

        [TestMethod]
        public void ListVerbatimSpec_ExtractsExactFile()
        {
            // a line exactly as `list` prints it (container\partition\path) works as an include spec
            var output = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var spec = $@"{InputName}\sda2\sda2.txt";
                RunExtract($@"extract --input ""{UncompressedSmallDrive}"" -o ""{output}"" --include ""{spec}""");

                var files = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
                Assert.AreEqual(1, files.Length, "expected exactly the one file named by the list line");
                Assert.AreEqual("sda2.txt", Path.GetFileName(files[0]));
                Assert.AreEqual(Sda2Md5, Utility.CalculateMD5(files[0]));
            }
            finally { Directory.Delete(output, true); }
        }
    }
}
