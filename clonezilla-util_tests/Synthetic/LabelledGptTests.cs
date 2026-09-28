using libPartclone;

namespace clonezilla_util_tests.Synthetic
{
    // A UEFI disk's partitions carry GPT labels, which 7-Zip puts in their names ("0.Basic data partition.ntfs"). The
    // drive-image check keyed on the first name being "0" plus an extension, so every labelled GPT disk was served as one
    // partition holding raw partition blobs (gh review F2). No test asset had a GPT label, so this builds a disk: the
    // small drive's two NTFS partitions, restored from their partclone images, behind a labelled GPT.
    [TestClass]
    [DoNotParallelize]
    public class LabelledGptTests : ExeTestBase
    {
        const string SmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";
        const string Sda1Md5 = "c3f38733914d360530455ba3b4073868";
        const string Sda2Md5 = "b80328235f5d991c6dc8982e1d1876bc";

        [TestMethod, Timeout(15 * 60_000)]
        public void PartitionsAreNamedByIndex()
        {
            var work = NewTempFolder();
            try
            {
                using (var sda1 = File.OpenRead(Path.Combine(SmallDrive, "sda1.ntfs-ptcl-img.uncomp")))
                using (var sda2 = File.OpenRead(Path.Combine(SmallDrive, "sda2.ntfs-ptcl-img.uncomp")))
                {
                    GptDiskImage.Write(Path.Combine(work, "labelled-gpt.img"),
                    [
                        ("Basic data partition", new PartcloneStream("labelled-gpt", "sda1", sda1)),
                        ("Second data partition", new PartcloneStream("labelled-gpt", "sda2", sda2)),
                    ]);
                }
                var cache = Path.Combine(work, "cache");

                //relative --input: the image file, from its own folder
                var records = RunExe(["list", "--input", "labelled-gpt.img", "--cache-folder", cache], workingDirectory: work).AssertSucceeded("list").Records();
                Assert.IsTrue(records.Contains(@"labelled-gpt\partition0\sda1.txt"), $"sda1.txt not listed in partition0. First records: {string.Join(" | ", records.Take(5))}");
                Assert.IsTrue(records.Contains(@"labelled-gpt\partition1\sda2.txt"), $"sda2.txt not listed in partition1. First records: {string.Join(" | ", records.Take(5))}");
                Assert.IsFalse(records.Any(r => r.EndsWith(".ntfs", StringComparison.OrdinalIgnoreCase)), "a partition was served as a raw blob inside a single partition");

                //the index names are what -p takes
                var partition1 = RunExe(["list", "--input", "labelled-gpt.img", "-p", "partition1", "--cache-folder", cache], workingDirectory: work).AssertSucceeded("list -p partition1").Records();
                Assert.IsTrue(partition1.Contains(@"labelled-gpt\partition1\sda2.txt"), "-p partition1 did not list sda2.txt");
                Assert.IsTrue(partition1.All(r => r.StartsWith(@"labelled-gpt\partition1\")), "-p partition1 listed another partition");

                var output = Path.Combine(work, "out");
                RunExe(["extract", "--input", Path.Combine(work, "labelled-gpt.img"), "-o", output, "--include", "*.txt", "--cache-folder", cache]).AssertSucceeded("extract");
                AssertFileMd5(Path.Combine(output, "partition0", "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(output, "partition1", "sda2.txt"), Sda2Md5);
            }
            finally { TryDeleteFolder(work); }
        }
    }
}
