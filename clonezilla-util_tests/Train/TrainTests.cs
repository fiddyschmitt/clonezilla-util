using libTrainCompress;
using libTrainCompress.Compressors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using libCommon;
using libCommon.Streams;
using libCommon.Streams.Sparse;
using System.Diagnostics;

namespace clonezilla_util_tests.Train
{
    [TestClass]
    public class TrainTests
    {
        [TestMethod]
        public void Zst()
        {
            TestTrain(
                @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img",
                new Compressor[] { new zstdCompressor() }
                );
        }

        [TestMethod]
        public void Gz()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestTrain(
                @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img",
                new Compressor[] { new gzCompressor() }
                );
        }

        [TestMethod]
        public void Xz()
        {
            TestTrain(
                @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img",
                new Compressor[] { new xzCompressor() }
                );
        }

        public static void TestTrain(string inputFilename, IList<Compressor> compressors)
        {
            var originalFileStream = File.OpenRead(inputFilename);

            var compressedStream = new MemoryStream();
            using (var trainCompressor = new TrainCompressor(compressedStream, compressors, 10 * 1024 * 1024))
            {
                originalFileStream.CopyTo(trainCompressor, 50 * 1024 * 1024, progress =>
                {
                    Debug.WriteLine($"Compressing {progress.Read.BytesToString()}");
                });
            }


            var uncompressedOutputStream = new MemoryStream();
            compressedStream.Seek(0, SeekOrigin.Begin);
            using (var trainDecompressor = new TrainDecompressor(compressedStream, compressors))
            {
                trainDecompressor.CopyTo(uncompressedOutputStream, 50 * 1024 * 1024, progress =>
                {
                    Debug.WriteLine($"Decompressing {progress.Read.BytesToString()}");
                });

                uncompressedOutputStream.Seek(0, SeekOrigin.Begin);
            }


            var originalMd5 = Utility.CalculateMD5(originalFileStream);
            var outputMd5 = Utility.CalculateMD5(uncompressedOutputStream);

            var success = originalMd5.Equals(outputMd5);
            Assert.IsTrue(success, "MD5 hashes do not match");


            //test random seeking
            uncompressedOutputStream = new MemoryStream();
            compressedStream.Seek(0, SeekOrigin.Begin);
            using (var trainDecompressor = new TrainDecompressor(compressedStream, compressors))
            {
                Utilities.Utility.TestSeeking(trainDecompressor, uncompressedOutputStream);

                outputMd5 = Utility.CalculateMD5(uncompressedOutputStream);

                success = originalMd5.Equals(outputMd5);
                Assert.IsTrue(success, "MD5 hashes do not match after random seeking");
            }
        }
    }
}
