using System.Text.Json;

namespace clonezilla_util_tests.Cache
{
    // A cache file cut short (a kill mid-write, a full disk) must be discarded and rebuilt, not trusted: a truncated file
    // list used to leave its partition empty on every later run (gh review F3), and a truncated bzip2 index made every
    // open of the image throw (gh review F4). Each test works in its own --cache-folder, damages a file the first run
    // wrote, and checks that the second run noticed (its warning), still lists correctly, and left a valid file behind.
    [TestClass]
    [DoNotParallelize]
    public class CacheSelfHealTests : ExeTestBase
    {
        [TestMethod, Timeout(15 * 60_000)]
        public void TruncatedFileList_IsRebuilt()
        {
            const string input = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";
            const string expected = @"2022-06-27-20-img_small_drive-uncompressed\sda1\sda1.txt";

            var cache = NewTempFolder();
            try
            {
                var first = RunExe(["list", "--input", input, "--cache-folder", cache]).AssertSucceeded("list");
                Assert.IsTrue(first.Records().Contains(expected), $"the first listing lacks {expected}");

                var fileList = SingleFile(cache, "sda1.Files.json");
                Truncate(fileList);

                var second = RunExe(["list", "--input", input, "--cache-folder", cache]).AssertSucceeded("list after truncating the file list");
                Assert.IsTrue(second.Stderr.Contains("could not be read"), $"no warning about the damaged file list. End of the log:{Environment.NewLine}{second.LogTail()}");
                CollectionAssert.AreEqual(first.Records(), second.Records(), "the listing after the rebuild differs from the original one");
                AssertJsonArray(fileList);
            }
            finally { TryDeleteFolder(cache); }
        }

        //sdb1 of the bzip2 image: the smallest real (single-stream) bzip2 partition, so building its index twice is quick.
        //The file list is deleted as well, or the second run would list from it without ever opening the index.
        [TestMethod, Timeout(60 * 60_000)]
        public void TruncatedBzip2Index_IsRebuilt()
        {
            const string input = @"E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2";
            const string expected = @"2022-07-16-22-img_pb-devops1_bzip2\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg";

            var cache = NewTempFolder();
            try
            {
                var first = RunExe(["list", "--input", input, "-p", "sdb1", "--cache-folder", cache]).AssertSucceeded("list");
                Assert.IsTrue(first.Records().Contains(expected), $"the first listing lacks {expected}");

                var index = SingleFile(cache, "sdb1.bzip2_index_v2.json");
                Truncate(index);
                File.Delete(SingleFile(cache, "sdb1.Files.json"));

                var second = RunExe(["list", "--input", input, "-p", "sdb1", "--cache-folder", cache]).AssertSucceeded("list after truncating the bzip2 index");
                Assert.IsTrue(second.Stderr.Contains("rebuilding it"), $"no warning about the damaged bzip2 index. End of the log:{Environment.NewLine}{second.LogTail()}");
                CollectionAssert.AreEqual(first.Records(), second.Records(), "the listing after the rebuild differs from the original one");
                AssertJsonArray(index);
            }
            finally { TryDeleteFolder(cache); }
        }

        static string SingleFile(string folder, string name)
        {
            var matches = Directory.GetFiles(folder, name, SearchOption.AllDirectories);
            Assert.AreEqual(1, matches.Length, $"expected one {name} under {folder}, found {matches.Length}");
            return matches[0];
        }

        //keeps the first half: a JSON array missing its tail, as a kill mid-write leaves it
        static void Truncate(string path)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.IsTrue(bytes.Length > 16, $"{path} is unexpectedly small ({bytes.Length} bytes)");
            File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        }

        static void AssertJsonArray(string path)
        {
            Assert.IsTrue(File.Exists(path), $"{path} was not rewritten");
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            Assert.AreEqual(JsonValueKind.Array, document.RootElement.ValueKind, $"{path} is not a JSON array");
            Assert.IsTrue(document.RootElement.GetArrayLength() > 0, $"{path} is empty");
        }
    }
}
