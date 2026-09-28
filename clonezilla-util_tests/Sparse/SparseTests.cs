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
    public class SparseTests : ExeTestBase
    {
        //sda1 of the gz image: small, and its MD5 is already pinned by ImageFileTests.Gz, which serves the same
        //partition image through a mount. This test used to check only that the output was sparse, so the
        //sparsify path could have zeroed real data and still passed (gh review F53).
        const string Input = @"E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz";
        const string Sda1Md5 = "d9a64d07801cb9f5878a72da4c5d53fd";

        //Size on disk / file size. sda1 is mostly a WinRE image, so even perfectly sparse output is 72.6%
        //allocated (measured 2026-09-24: 418 MB of 576 MB); an output that wrote its zeros is 100%.
        const double MaxAllocatedFraction = 0.9;

        //runaway protection only: 35 s warm (2026-09-24)
        [TestMethod, Timeout(20 * 60_000)]
        public void ExtractAndSparsifyFile()
        {
            var allocatedFraction = ExtractSda1AndMeasure([]);
            Assert.IsTrue(allocatedFraction < MaxAllocatedFraction, $"Size on disk is {allocatedFraction:P1} of the file size; the output is not sparse");
        }

        //--no-sparse-output: the same bytes, written in full (every cluster allocated)
        [TestMethod, Timeout(20 * 60_000)]
        public void ExtractWithoutSparse_WritesEveryByte()
        {
            var allocatedFraction = ExtractSda1AndMeasure(["--no-sparse-output"]);
            Assert.IsTrue(allocatedFraction > 0.99, $"Size on disk is only {allocatedFraction:P1} of the file size; --no-sparse-output still produced a sparse file");
        }

        //Extracts sda1, checks its content, and returns its size on disk as a fraction of its length.
        double ExtractSda1AndMeasure(string[] extraArgs)
        {
            var outputFolder = NewTempFolder();
            try
            {
                RunExe(["extract-partition-image", "--input", Input, "--output", outputFolder, "-p", "sda1", .. extraArgs])
                    .AssertSucceeded("extract-partition-image");

                var extractedFilename = Path.Combine(outputFolder, "sda1.img");
                Assert.IsTrue(File.Exists(extractedFilename), $"missing {extractedFilename}");
                Assert.AreEqual(Sda1Md5, Utility.CalculateMD5(extractedFilename), "the extracted partition image has the wrong content");

                var fileSize = new FileInfo(extractedFilename).Length;
                var sizeOnDisk = GetFileSizeOnDisk(extractedFilename);
                return sizeOnDisk / (double)fileSize;
            }
            finally
            {
                TryDeleteFolder(outputFolder);
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
