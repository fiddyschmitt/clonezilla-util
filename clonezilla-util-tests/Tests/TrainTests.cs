using libCommon.Streams;
using libTrainCompress;
using libTrainCompress.Compressors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using libCommon;
using libCommon.Streams.Sparse;

namespace clonezilla_util_tests.Tests
{
    public static class TrainTests
    {
        public static void Test()
        {
            //Test_gz();
            //Test_xz();
            TestZstandard();
        }

        //Not working yet. The Reads are super slow for some reason (SubStream is asked for 8,192 bytes at a time)
        public static void Test_gz()
        {
            var filename = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img";
            //filename = @"C:\Users\fiddy\Desktop\dev\cs\ClonezillaApps\clonezilla-util\bin\Debug\net6.0\cache\4b18d7657aee7a3887f09d883b08198e\cache.train";   //44GB

            var compressors = new List<Compressor>()
            {
                new gzCompressor()
            };

            var compressedStream = new MemoryStream();
            //var compressedStream = File.Create(@"C:\Temp\1 - compressed.train");
            using (var trainCompressor = new TrainCompressor(compressedStream, compressors, 10 * 1024 * 1024))
            {
                var originalFileStream = File.OpenRead(filename);
                //originalFileStream.CopyTo(trainCompressor);
                originalFileStream.CopyTo(trainCompressor, 50 * 1024 * 1024, progress =>
                {
                    //Console.WriteLine($"Compressing {progress.BytesToString()}");
                });
            }


            var uncompressedOutputStream = new MemoryStream();
            //var uncompressedOutputStream = File.Create(@"C:\Temp\2 - decompressed.bin");
            compressedStream.Seek(0, SeekOrigin.Begin);
            using (var trainDecompressor = new TrainDecompressor(compressedStream, compressors))
            {
                //trainDecompressor.CopyTo(uncompressedOutputStream, 50 * 1024 * 1024);

                trainDecompressor.CopyTo(uncompressedOutputStream, 1024 * 1024, progress =>
                {
                    //Console.WriteLine($"Decompressing {progress.BytesToString()}");
                });

                uncompressedOutputStream.Seek(0, SeekOrigin.Begin);
            }


            var originalMd5 = libCommon.Utility.CalculateMD5(filename);
            var outputMd5 = libCommon.Utility.CalculateMD5(uncompressedOutputStream);

            var success = originalMd5.Equals(outputMd5);

            if (success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"Success");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"Fail");
            }

            Console.ResetColor();
            Console.WriteLine($": CompressAndDecompress gz");
        }

        public static void Test_xz()
        {
            var filename = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img";
            //filename = @"C:\Users\fiddy\Desktop\dev\cs\ClonezillaApps\clonezilla-util\bin\Debug\net6.0\cache\4b18d7657aee7a3887f09d883b08198e\cache.train";   //44GB

            var compressors = new List<Compressor>()
            {
                new xzCompressor()
            };

            var compressedStream = new MemoryStream();
            //var compressedStream = File.Create(@"C:\Temp\1 - compressed.train");
            using (var trainCompressor = new TrainCompressor(compressedStream, compressors, 10 * 1024 * 1024))
            {
                var originalFileStream = File.OpenRead(filename);
                //originalFileStream.CopyTo(trainCompressor);
                originalFileStream.CopyTo(trainCompressor, 50 * 1024 * 1024, progress =>
                {
                    //Console.WriteLine($"Compressing {progress.BytesToString()}");
                });
            }


            var uncompressedOutputStream = new MemoryStream();
            //var uncompressedOutputStream = File.Create(@"C:\Temp\2 - decompressed.bin");
            compressedStream.Seek(0, SeekOrigin.Begin);
            using (var trainDecompressor = new TrainDecompressor(compressedStream, compressors))
            {
                //trainDecompressor.CopyTo(uncompressedOutputStream, 50 * 1024 * 1024);
                trainDecompressor.CopyTo(uncompressedOutputStream, 50 * 1024 * 1024, progress =>
                {
                    Console.WriteLine($"Decompressing {progress.BytesToString()}");
                });

                uncompressedOutputStream.Seek(0, SeekOrigin.Begin);
            }


            var originalMd5 = libCommon.Utility.CalculateMD5(filename);
            var outputMd5 = libCommon.Utility.CalculateMD5(uncompressedOutputStream);

            var success = originalMd5.Equals(outputMd5);

            if (success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"Success");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"Fail");
            }

            Console.ResetColor();
            Console.WriteLine($": CompressAndDecompress xz");
        }

        public static void TestZstandard()
        {
            var filename = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img";
            //filename = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2022-01-28_xp_with_autocad_sda.img";
            //filename = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda.img";

            var compressors = new List<Compressor>()
            {
                new zstdCompressor()
            };

            /*
            using (var compressor = new ZstdNet.CompressionStream(File.OpenWrite(@"C:\Temp\compressed-zst.bin")))
            using (var inputFs = File.OpenRead(filename))
            {
                inputFs.CopyTo(compressor, 50 * 1024 * 1024, progress =>
                {
                    Console.WriteLine($"Zst Compressing {progress.BytesToString()}");
                });
            }
            */

            //var compressedStream = new MemoryStream();
            var compressedStream = File.Create(@"E:\Temp\compressed.bin");
            using (var trainCompressor = new TrainCompressor(compressedStream, compressors, 10 * 1024 * 1024))
            {
                var originalFileStream = File.OpenRead(filename);
                //originalFileStream.CopyTo(trainCompressor);
                originalFileStream.CopyTo(trainCompressor, 50 * 1024 * 1024, progress =>
                {
                    //Console.WriteLine($"Compressing {progress.BytesToString()}");
                });
            }


            //var uncompressedOutputStream = new MemoryStream();
            var uncompressedOutputStream = File.Create(@"E:\Temp\decompressed.bin");
            compressedStream.Seek(0, SeekOrigin.Begin);

            var sparseAwareTrainDecompressor = new SparseAwareReader(new TrainDecompressor(compressedStream, compressors));
            StreamUtility.ExtractToFile("", compressedStream, sparseAwareTrainDecompressor, uncompressedOutputStream, true);

            /*
            using (var trainDecompressor = new TrainDecompressor(compressedStream, compressors))
            {
                //trainDecompressor.CopyTo(uncompressedOutputStream, 50 * 1024 * 1024);
                trainDecompressor.CopyTo(uncompressedOutputStream, 50 * 1024 * 1024, progress =>
                {
                    Console.WriteLine($"Decompressing {progress.BytesToString()}");
                });

                uncompressedOutputStream.Seek(0, SeekOrigin.Begin);
            }
            */

            uncompressedOutputStream.Seek(0, SeekOrigin.Begin);

            var originalMd5 = libCommon.Utility.CalculateMD5(filename);
            var outputMd5 = libCommon.Utility.CalculateMD5(uncompressedOutputStream);

            var success = originalMd5.Equals(outputMd5);

            if (success)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"Success");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"Fail");
            }

            Console.ResetColor();
            Console.WriteLine($": CompressAndDecompress Zstandard");


            //test random seeking
            uncompressedOutputStream = File.Create(@"E:\Temp\decompressed-2_random_seeking.bin");
            compressedStream.Seek(0, SeekOrigin.Begin);
            using (var trainDecompressor = new TrainDecompressor(compressedStream, compressors))
            {
                Utility.TestSeeking(trainDecompressor, uncompressedOutputStream);
            }


            File.Delete(@"E:\Temp\compressed.bin");
            File.Delete(@"E:\Temp\decompressed.bin");
            File.Delete(@"E:\Temp\decompressed-2_random_seeking.bin");
        }
    }
}
