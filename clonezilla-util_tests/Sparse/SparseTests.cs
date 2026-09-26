using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using libCommon;
using Windows.Win32;

namespace clonezilla_util_tests.Sparse
{
    [TestClass]
    public class SparseTests
    {
        //sda1 of the gz image: small, and its MD5 is already pinned by ImageFileTests.Gz, which serves the same
        //partition image through a mount. This test used to check only that the output was sparse, so the
        //sparsify path could have zeroed real data and still passed (gh review F53).
        const string Input = @"E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz";
        const string Sda1Md5 = "d9a64d07801cb9f5878a72da4c5d53fd";

        //Size on disk / file size. sda1 is mostly a WinRE image, so even perfectly sparse output is 72.6%
        //allocated (measured 2026-09-24: 418 MB of 576 MB); an output that wrote its zeros is 100%.
        const double MaxAllocatedFraction = 0.9;

        //Registered so [TestCleanup] can kill it: a [Timeout] abandons the test without running finally blocks.
        static Process? current;

        [TestCleanup]
        public void KillLeakedExe()
        {
            var process = Interlocked.Exchange(ref current, null);
            try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }

        //runaway protection only: 35 s warm (2026-09-24)
        [TestMethod, Timeout(20 * 60_000)]
        public void ExtractAndSparsifyFile()
        {
            var outputFolder = Directory.CreateTempSubdirectory().FullName;
            try
            {
                var args = @$"extract-partition-image --input ""{Input}"" --output ""{outputFolder}"" -p sda1";

                var psi = new ProcessStartInfo(Main.ExeUnderTest, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi) ?? throw new Exception($"Could not start {Main.ExeUnderTest}");
                current = process;
                process.WaitForExit();
                Interlocked.CompareExchange(ref current, null, process);
                Assert.AreEqual(0, process.ExitCode, "extract-partition-image exited with a non-zero code");

                var extractedFilename = Path.Combine(outputFolder, "sda1.img");
                Assert.IsTrue(File.Exists(extractedFilename), $"missing {extractedFilename}");
                Assert.AreEqual(Sda1Md5, Utility.CalculateMD5(extractedFilename), "the extracted partition image has the wrong content");

                var fileSize = new FileInfo(extractedFilename).Length;
                var sizeOnDisk = GetFileSizeOnDisk(extractedFilename);
                var allocatedFraction = sizeOnDisk / (double)fileSize;
                Assert.IsTrue(allocatedFraction < MaxAllocatedFraction, $"Size on disk is {allocatedFraction:P1} of the file size; the output is not sparse");
            }
            finally
            {
                Directory.Delete(outputFolder, true);
            }
        }

        public static long GetFileSizeOnDisk(string file)
        {
            var info = new FileInfo(file);
            if (info == null) return -1;
            if (info.Directory == null) return -1;
            var result = PInvoke.GetDiskFreeSpace(info.Directory.Root.FullName, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _);
            if (!result) throw new Win32Exception();
            uint clusterSize = sectorsPerCluster * bytesPerSector;
            uint losize = PInvoke.GetCompressedFileSize(file, out uint hosize);
            long size;
            size = (long)hosize << 32 | losize;
            return ((size + clusterSize - 1) / clusterSize) * clusterSize;
        }
    }
}
