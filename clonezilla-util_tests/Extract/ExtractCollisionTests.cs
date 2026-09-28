using libCommon;

namespace clonezilla_util_tests.Extract
{
    // extract must never let two archive entries overwrite each other on the host: --flatten maps different folders onto
    // one name, and a case-sensitive source (ext4) can hold names that NTFS treats as one. The second writer gets a
    // " (2)" suffix instead (gh review F11). Collisions are resolved in extraction order, which is parallel within a
    // partition, so these tests check that every source file survived with its own content - not which one kept the
    // plain name.
    [TestClass]
    [DoNotParallelize]
    public class ExtractCollisionTests : ExeTestBase
    {
        const string UncompressedSmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";
        const string Lz4SmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-09-12-20-img_small_drive_using_lz4";
        const string Sda1Md5 = "c3f38733914d360530455ba3b4073868";
        const string Sda2Md5 = "b80328235f5d991c6dc8982e1d1876bc";

        //Every NTFS volume has a $Boot, so --flatten over both partitions of the small drive maps two different files
        //onto one name. Their content differs (each boot sector carries its own volume serial and size), and preserve
        //mode, which cannot collide, says which content belongs to which partition.
        [TestMethod, Timeout(15 * 60_000)]
        public void Flatten_CollisionAcrossPartitions_KeepsBoth()
        {
            var work = NewTempFolder();
            try
            {
                var flat = Path.Combine(work, "flat");
                var preserved = Path.Combine(work, "preserved");
                RunExe(["extract", "--input", UncompressedSmallDrive, "-o", flat, "--include", "$Boot", "--flatten"]).AssertSucceeded("extract --flatten");
                RunExe(["extract", "--input", UncompressedSmallDrive, "-o", preserved, "--include", "$Boot"]).AssertSucceeded("extract");

                var sda1Boot = Utility.CalculateMD5(Path.Combine(preserved, "sda1", "[SYSTEM]", "$Boot"));
                var sda2Boot = Utility.CalculateMD5(Path.Combine(preserved, "sda2", "[SYSTEM]", "$Boot"));
                Assert.AreNotEqual(sda1Boot, sda2Boot, "the two partitions' $Boot should differ, or this test cannot tell them apart");

                var flattened = Directory.GetFiles(flat).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList();
                CollectionAssert.AreEqual(new[] { "$Boot", "$Boot (2)" }, flattened, $"flattened output: {string.Join(", ", flattened)}");

                var flattenedMd5s = flattened.Select(name => Utility.CalculateMD5(Path.Combine(flat, name!))).OrderBy(md5 => md5).ToList();
                CollectionAssert.AreEqual(new[] { sda1Boot, sda2Boot }.OrderBy(md5 => md5).ToList(), flattenedMd5s, "one of the colliding files was overwritten");
            }
            finally { TryDeleteFolder(work); }
        }

        //Two containers holding the same drive: with several containers, preserve mode prefixes each partition folder
        //with its container's name; --flatten collides every file with its twin across containers.
        [TestMethod, Timeout(15 * 60_000)]
        public void MultipleContainers()
        {
            var work = NewTempFolder();
            try
            {
                var preserved = Path.Combine(work, "preserved");
                RunExe(["extract", "--input", UncompressedSmallDrive, Lz4SmallDrive, "-o", preserved, "--include", "*.txt"]).AssertSucceeded("extract");

                AssertFileMd5(Path.Combine(preserved, "2022-06-27-20-img_small_drive-uncompressed.sda1", "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(preserved, "2022-06-27-20-img_small_drive-uncompressed.sda2", "sda2.txt"), Sda2Md5);
                AssertFileMd5(Path.Combine(preserved, "2022-09-12-20-img_small_drive_using_lz4.sda1", "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(preserved, "2022-09-12-20-img_small_drive_using_lz4.sda2", "sda2.txt"), Sda2Md5);
                Assert.AreEqual(4, Directory.GetFiles(preserved, "*", SearchOption.AllDirectories).Length);

                var flat = Path.Combine(work, "flat");
                RunExe(["extract", "--input", UncompressedSmallDrive, Lz4SmallDrive, "-o", flat, "--include", "*.txt", "--flatten"]).AssertSucceeded("extract --flatten");

                AssertFileMd5(Path.Combine(flat, "sda1.txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(flat, "sda1 (2).txt"), Sda1Md5);
                AssertFileMd5(Path.Combine(flat, "sda2.txt"), Sda2Md5);
                AssertFileMd5(Path.Combine(flat, "sda2 (2).txt"), Sda2Md5);
                Assert.AreEqual(4, Directory.GetFiles(flat).Length);
            }
            finally { TryDeleteFolder(work); }
        }

        //Ubuntu's ext4 root holds usr/include/X11/bitmaps/Stipple (96 bytes, modified 2022-03-25 09:53:32 UTC) and
        //.../stipple (133 bytes, 11:47:31 UTC) in one folder; on NTFS they are one name, so preserve mode collides too.
        //The include matches both (it is case-insensitive) and nothing else. Whichever is written second gets " (2)",
        //keeping its own case, so each output file still names the entry it came from.
        [TestMethod, Timeout(120 * 60_000)]
        public void CaseOnlyCollision_KeepsBoth()
        {
            var output = NewTempFolder();
            try
            {
                RunExe(["extract", "--input", @"E:\clonezilla-util-test resources\clonezilla images\2022-09-13-12-img_ubuntu_22.04_default_filesystem",
                        "-p", "sda3", "-o", output, "--include", "stipple"])
                    .AssertSucceeded("extract");

                var folder = Path.Combine(output, "sda3", "usr", "include", "X11", "bitmaps");
                var files = Directory.GetFiles(folder).Select(path => new FileInfo(path)).ToList();
                Assert.AreEqual(2, files.Count, $"expected both files, found: {string.Join(", ", files.Select(f => f.Name))}");
                Assert.AreEqual(1, files.Count(f => f.Name.EndsWith(" (2)", StringComparison.Ordinal)), "exactly one of the pair should carry the collision suffix");

                var expected = new Dictionary<string, (long Size, DateTime Modified)>(StringComparer.Ordinal)
                {
                    ["Stipple"] = (96, new DateTime(2022, 3, 25, 9, 53, 32, DateTimeKind.Utc)),
                    ["stipple"] = (133, new DateTime(2022, 3, 25, 11, 47, 31, DateTimeKind.Utc)),
                };
                foreach (var file in files)
                {
                    var sourceName = file.Name.EndsWith(" (2)", StringComparison.Ordinal) ? file.Name[..^4] : file.Name;
                    Assert.IsTrue(expected.TryGetValue(sourceName, out var source), $"unexpected output name '{file.Name}'");
                    Assert.AreEqual(source.Size, file.Length, $"'{file.Name}' does not hold the content of '{sourceName}'");
                    Assert.IsTrue((file.LastWriteTimeUtc - source.Modified).Duration() < TimeSpan.FromSeconds(1), $"'{file.Name}' has the timestamp {file.LastWriteTimeUtc:O}, not that of '{sourceName}'");
                }
            }
            finally { TryDeleteFolder(output); }
        }
    }
}
