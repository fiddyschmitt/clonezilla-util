using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;

namespace clonezilla_util_tests.Sparse
{
    [TestClass]
    public class SparseTests
    {
        [TestMethod]
        public void ExtractAndSparsifyFile()
        {
            var outputFolder = Directory.CreateTempSubdirectory().FullName;

            var args = @$"extract-partition-image --input ""E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz"" --output ""{outputFolder}"" -p sda2";

            var psi = new ProcessStartInfo(Main.ExeUnderTest, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var process = Process.Start(psi);
            process?.WaitForExit();

            var extractedFilename = Directory.GetFiles(outputFolder).First();
            var fileSize = new FileInfo(extractedFilename).Length;
            var sizeOnDisk = GetFileSizeOnDisk(extractedFilename);
            Directory.Delete(outputFolder, true);

            var success = sizeOnDisk / (double)fileSize < 0.5;
            Assert.IsTrue(success, "Size On Disk is not smaller than file size");
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
