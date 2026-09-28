namespace clonezilla_util_tests.ListContents
{
    // The shape of list's output, which scripts consume: one record per entry, container\partition\path, separated by
    // "\n" (or --separator / --null-separator), UTF-8 without a BOM on stdout, and nothing but records there - the log
    // goes to stderr (gh review F47). Each test uses its own cache folder, so the first listing is always the fresh
    // enumeration and a second run can be compared against it as the cached listing.
    [TestClass]
    [DoNotParallelize]
    public class OutputFormat : ExeTestBase
    {
        const string UncompressedSmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";
        const string InputName = "2022-06-27-20-img_small_drive-uncompressed";
        const int TimeoutMs = 15 * 60_000;

        ExeResult List(string cacheFolder, params string[] extraArgs) =>
            RunExe(["list", "--input", UncompressedSmallDrive, "--cache-folder", cacheFolder, .. extraArgs]).AssertSucceeded("list");

        [TestMethod, Timeout(TimeoutMs)]
        public void DefaultFormat()
        {
            var cache = NewTempFolder();
            try
            {
                var fresh = List(cache);

                Assert.IsFalse(fresh.Stdout.StartsWith('﻿'), "stdout starts with a UTF-8 BOM");
                Assert.IsFalse(fresh.Stdout.Contains('\r'), "records are separated by \"\\n\", not \"\\r\\n\"");
                Assert.IsTrue(fresh.Stdout.EndsWith('\n'), "the last record is terminated too");
                Assert.IsFalse(fresh.Stdout.Contains("clonezilla-util v"), "the log banner leaked into stdout");
                Assert.IsTrue(fresh.Stderr.Contains("clonezilla-util v"), "the log (with its banner) should be on stderr");

                var records = fresh.Records();
                Assert.IsTrue(records.Contains($@"{InputName}\sda1\sda1.txt"), "sda1.txt not listed");
                Assert.IsTrue(records.Contains($@"{InputName}\sda2\sda2.txt"), "sda2.txt not listed");
                var foreign = records.Where(r => !r.StartsWith($@"{InputName}\sda1\") && !r.StartsWith($@"{InputName}\sda2\")).ToList();
                Assert.AreEqual(0, foreign.Count, $"records not of the form container\\partition\\path: {string.Join(" | ", foreign.Take(5))}");

                //the second run lists from the cache folder the first one filled; it must print exactly the same
                var cached = List(cache);
                CollectionAssert.AreEqual(records, cached.Records(), "the cached listing differs from the fresh one");
            }
            finally { TryDeleteFolder(cache); }
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void Separators()
        {
            var cache = NewTempFolder();
            try
            {
                var byLine = List(cache).Records();

                var byNull = List(cache, "--null-separator");
                Assert.IsFalse(byNull.Stdout.Contains('\n'), "--null-separator output contains a newline");
                CollectionAssert.AreEqual(byLine, byNull.Records('\0'), "--null-separator lists different records");

                var byPipe = List(cache, "--separator", "|");
                CollectionAssert.AreEqual(byLine, byPipe.Records('|'), "--separator | lists different records");
            }
            finally { TryDeleteFolder(cache); }
        }

        //--verbose (Debug logging) must not leak into stdout, --process-trailing-nulls does not change what a partition
        //holds, and --temp-folder is accepted but ignored with a warning: nothing is written there (gh review F49).
        //Separate cache folders, so both runs enumerate the partitions afresh with their own options.
        [TestMethod, Timeout(TimeoutMs)]
        public void OptionsDoNotChangeTheListing()
        {
            var work = NewTempFolder();
            try
            {
                var plain = List(Path.Combine(work, "cache-plain")).Records();

                var tempFolder = Path.Combine(work, "never-created");
                var withOptions = List(Path.Combine(work, "cache-options"), "--verbose", "--process-trailing-nulls", "--temp-folder", tempFolder);

                CollectionAssert.AreEqual(plain, withOptions.Records(), "the options changed the listing");
                Assert.IsTrue(withOptions.Stderr.Contains("--temp-folder is ignored"), "no warning that --temp-folder is ignored");
                Assert.IsFalse(Directory.Exists(tempFolder), "something was written to the --temp-folder");
            }
            finally { TryDeleteFolder(work); }
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void PartitionFilter()
        {
            var cache = NewTempFolder();
            try
            {
                var records = List(cache, "-p", "sda2").Records();

                Assert.IsTrue(records.Contains($@"{InputName}\sda2\sda2.txt"), "sda2.txt not listed");
                Assert.IsTrue(records.All(r => r.StartsWith($@"{InputName}\sda2\")), "-p sda2 listed another partition");
            }
            finally { TryDeleteFolder(cache); }
        }

        //A trailing separator on --input once gave the container an empty name, so records began "\sda1\..." (gh review F10)
        [TestMethod, Timeout(TimeoutMs)]
        [DataRow(UncompressedSmallDrive + @"\")]
        [DataRow(@"E:/clonezilla-util-test resources/clonezilla images/2022-06-27-20-img_small_drive-uncompressed/")]
        public void TrailingSeparatorInput_KeepsContainerName(string input)
        {
            var cache = NewTempFolder();
            try
            {
                var records = RunExe(["list", "--input", input, "--cache-folder", cache]).AssertSucceeded("list").Records();

                Assert.IsTrue(records.Contains($@"{InputName}\sda1\sda1.txt"), $"expected {InputName}\\sda1\\sda1.txt; first records: {string.Join(" | ", records.Take(3))}");
            }
            finally { TryDeleteFolder(cache); }
        }

        //A relative --input names its container after the folder it resolves to - including ".", which used to name the
        //container "." itself. Run from a throwaway copy of the image: the exe writes its log under its working directory.
        [TestMethod, Timeout(TimeoutMs)]
        [DataRow(InputName, "")]
        [DataRow(@".\" + InputName + @"\", "")]
        [DataRow(".", InputName)]
        public void RelativeInput(string input, string workingSubfolder)
        {
            var work = NewTempFolder();
            try
            {
                CopyFolderFiles(UncompressedSmallDrive, Path.Combine(work, InputName));

                var records = RunExe(["list", "--input", input, "--cache-folder", Path.Combine(work, "cache")], workingDirectory: Path.Combine(work, workingSubfolder))
                    .AssertSucceeded("list")
                    .Records();

                Assert.IsTrue(records.Contains($@"{InputName}\sda1\sda1.txt"), $"expected {InputName}\\sda1\\sda1.txt; first records: {string.Join(" | ", records.Take(3))}");
                Assert.IsTrue(records.Contains($@"{InputName}\sda2\sda2.txt"), $"expected {InputName}\\sda2\\sda2.txt");
            }
            finally { TryDeleteFolder(work); }
        }
    }
}
