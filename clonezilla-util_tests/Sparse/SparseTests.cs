using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

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

            var psi = new ProcessStartInfo(Main.ExeUnderTest, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
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
            uint dummy, sectorsPerCluster, bytesPerSector;
            int result = GetDiskFreeSpaceW(info.Directory.Root.FullName, out sectorsPerCluster, out bytesPerSector, out dummy, out dummy);
            if (result == 0) throw new Win32Exception();
            uint clusterSize = sectorsPerCluster * bytesPerSector;
            uint hosize;
            uint losize = GetCompressedFileSizeW(file, out hosize);
            long size;
            size = (long)hosize << 32 | losize;
            return ((size + clusterSize - 1) / clusterSize) * clusterSize;
        }

        [DllImport("kernel32.dll")]
        static extern uint GetCompressedFileSizeW([In, MarshalAs(UnmanagedType.LPWStr)] string lpFileName,
           [Out, MarshalAs(UnmanagedType.U4)] out uint lpFileSizeHigh);

        [DllImport("kernel32.dll", SetLastError = true, PreserveSig = true)]
        static extern int GetDiskFreeSpaceW([In, MarshalAs(UnmanagedType.LPWStr)] string lpRootPathName,
           out uint lpSectorsPerCluster, out uint lpBytesPerSector, out uint lpNumberOfFreeClusters,
           out uint lpTotalNumberOfClusters);
    }
}
