using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;

namespace clonezilla_util_tests.Synthetic
{
    // Clonezilla's parallel bzip2 (pbzip2) writes one independent bzip2 stream per chunk, concatenated. Sequential decoding
    // stopped after the first stream, so extract-partition-image produced a truncated partition (gh review F5). No test
    // image was made that way, so this builds one: the small drive's partclone images recompressed as 500,000-byte streams
    // (pbzip2 -b5; small enough that every stream holds exactly one bzip2 block, as pbzip2's do).
    [TestClass]
    [DoNotParallelize]
    public class ParallelBzip2Tests : ExeTestBase
    {
        const string SmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";
        const string Sda1Md5 = "c3f38733914d360530455ba3b4073868";
        const string Sda2Md5 = "b80328235f5d991c6dc8982e1d1876bc";
        const int StreamChunkBytes = 500_000;

        [TestMethod, Timeout(20 * 60_000)]
        public void MultiStreamImage_DecodesEveryStream()
        {
            var work = NewTempFolder();
            try
            {
                const string imageName = "2022-06-27-20-img_small_drive-pbzip2";
                var image = Path.Combine(work, imageName);
                Directory.CreateDirectory(image);
                foreach (var file in Directory.GetFiles(SmallDrive))
                {
                    var name = Path.GetFileName(file);
                    if (name.EndsWith(".uncomp", StringComparison.Ordinal))
                    {
                        WriteMultiStreamBzip2(file, Path.Combine(image, Path.ChangeExtension(name, ".bz2")));   //sda1.ntfs-ptcl-img.bz2
                    }
                    else
                    {
                        File.Copy(file, Path.Combine(image, name));
                    }
                }

                //extract-partition-image decodes sequentially, so it must read on past the end of every stream: its output
                //must be byte-identical to the partition restored from the uncompressed original. (Own cache folders: the
                //copy shares the original's IMG_ID.)
                var fromBzip2 = Path.Combine(work, "from-bzip2");
                var fromOriginal = Path.Combine(work, "from-original");
                RunExe(["extract-partition-image", "--input", image, "-o", fromBzip2, "--cache-folder", Path.Combine(work, "cache-bzip2")]).AssertSucceeded("extract-partition-image (bzip2)");
                RunExe(["extract-partition-image", "--input", SmallDrive, "-o", fromOriginal, "--cache-folder", Path.Combine(work, "cache-original")]).AssertSucceeded("extract-partition-image (original)");
                foreach (var partition in new[] { "sda1", "sda2" })
                {
                    var expected = Path.Combine(fromOriginal, $"{partition}.img");
                    var actual = Path.Combine(fromBzip2, $"{partition}.img");
                    Assert.AreEqual(new FileInfo(expected).Length, new FileInfo(actual).Length, $"{partition}.img from the multi-stream image has the wrong length");
                    Assert.AreEqual(libCommon.Utility.CalculateMD5(expected), libCommon.Utility.CalculateMD5(actual), $"{partition}.img from the multi-stream image has the wrong content");
                }

                //list and extract read through the bzip2 block index (random access), which must span the streams too
                var records = RunExe(["list", "--input", image, "--cache-folder", Path.Combine(work, "cache-bzip2")]).AssertSucceeded("list").Records();
                Assert.IsTrue(records.Contains($@"{imageName}\sda1\sda1.txt"), "sda1.txt not listed");
                Assert.IsTrue(records.Contains($@"{imageName}\sda2\sda2.txt"), "sda2.txt not listed");

                var output = Path.Combine(work, "files");
                RunExe(["extract", "--input", image, "-o", output, "--include", "*.txt", "--cache-folder", Path.Combine(work, "cache-bzip2")]).AssertSucceeded("extract");
                AssertFileMd5(Path.Combine(output, "sda1", "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(output, "sda2", "sda2.txt"), Sda2Md5);
            }
            finally { TryDeleteFolder(work); }
        }

        //each chunk compressed as a complete bzip2 stream of its own (header, block, end-of-stream marker), back to back
        static void WriteMultiStreamBzip2(string source, string destination)
        {
            var content = File.ReadAllBytes(source);
            Assert.IsTrue(content.Length > 2 * StreamChunkBytes, $"{source} is too small to span several streams");

            using var output = File.Create(destination);
            for (var offset = 0; offset < content.Length; offset += StreamChunkBytes)
            {
                using var stream = BZip2Stream.Create(output, CompressionMode.Compress, false, leaveOpen: true);
                stream.Write(content, offset, Math.Min(StreamChunkBytes, content.Length - offset));
            }
        }
    }
}
