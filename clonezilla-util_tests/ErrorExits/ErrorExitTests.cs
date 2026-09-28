namespace clonezilla_util_tests.ErrorExits
{
    // Exit codes are the command line's contract with scripts (Program.ReturnCode): 0 success, 1 invalid arguments,
    // 2 unexpected error, 3 partial extract, 4 Dokan not installed, 5 mount failed. A user's mistake must end quickly,
    // with a message naming the problem and no stack trace. Not covered: 4 cannot happen on a machine that runs the mount
    // tests, and 3 needs an archive entry that cannot be written, which no test image has.
    [TestClass]
    [DoNotParallelize]
    public class ErrorExitTests : ExeTestBase
    {
        const string SmallDrive = @"E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed";

        //each of these fails (or finishes) in seconds; the bounded wait turns a hang into a clear failure
        static readonly TimeSpan Quick = TimeSpan.FromMinutes(3);
        const int TimeoutMs = 5 * 60_000;

        static void AssertNoStackTrace(ExeResult result) =>
            Assert.IsFalse(result.Stderr.Contains("   at "), $"a user error printed a stack trace:{Environment.NewLine}{result.LogTail()}");

        [TestMethod, Timeout(TimeoutMs)]
        [DataRow("--help")]
        [DataRow("--version")]
        [DataRow("list --help")]
        public void HelpAndVersion_ExitZero(string args) =>
            RunExe(args, maxWait: Quick).AssertExitCode(0, args);

        [TestMethod, Timeout(TimeoutMs)]
        [DataRow("no-such-verb")]
        [DataRow("list")]
        [DataRow("list --input")]
        public void UnparsableCommandLine_ExitsOne(string args) =>
            RunExe(args, maxWait: Quick).AssertExitCode(1, args);

        [TestMethod, Timeout(TimeoutMs)]
        public void InputNotFound_ExitsOne()
        {
            const string missing = @"C:\no such folder\2022-06-27-20-img_small_drive-uncompressed";
            var result = RunExe(["list", "--input", missing], maxWait: Quick).AssertExitCode(1, "list of a missing input");

            Assert.IsTrue(result.Stderr.Contains(missing), $"the message should name the missing input:{Environment.NewLine}{result.LogTail()}");
            AssertNoStackTrace(result);
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void FolderThatIsNotAnImage_ExitsOne()
        {
            var folder = NewTempFolder();
            try
            {
                var result = RunExe(["list", "--input", folder, "--cache-folder", Path.Combine(folder, "cache")], maxWait: Quick).AssertExitCode(1, "list of a plain folder");

                Assert.IsTrue(result.Stderr.Contains("clonezilla-img"), $"the message should say what an image folder needs:{Environment.NewLine}{result.LogTail()}");
                AssertNoStackTrace(result);
            }
            finally { TryDeleteFolder(folder); }
        }

        //a Clonezilla folder (it has the clonezilla-img marker) with no partition images in it
        [TestMethod, Timeout(TimeoutMs)]
        public void ImageWithNoPartitions_ExitsOne()
        {
            var folder = NewTempFolder();
            try
            {
                File.WriteAllText(Path.Combine(folder, "clonezilla-img"), "");
                var result = RunExe(["list", "--input", folder, "--cache-folder", Path.Combine(folder, "cache")], maxWait: Quick).AssertExitCode(1, "list of an image with no partitions");

                Assert.IsTrue(result.Stderr.Contains("No partitions to list"), $"unexpected message:{Environment.NewLine}{result.LogTail()}");
                AssertNoStackTrace(result);
            }
            finally { TryDeleteFolder(folder); }
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void UnknownPartition_ExitsOneAndNamesTheValidOnes()
        {
            var result = RunExe(["list", "--input", SmallDrive, "-p", "sdz9"], maxWait: Quick).AssertExitCode(1, "list -p sdz9");

            Assert.IsTrue(result.Stderr.Contains("sdz9"), $"the message should name the bad partition:{Environment.NewLine}{result.LogTail()}");
            Assert.IsTrue(result.Stderr.Contains("Valid partitions are: sda1, sda2"), $"the message should list the valid partitions:{Environment.NewLine}{result.LogTail()}");
            AssertNoStackTrace(result);
        }

        //the output folder is created only once the inputs have loaded, so a failed extract leaves nothing behind (gh review F58)
        [TestMethod, Timeout(TimeoutMs)]
        public void ExtractWithBadInput_CreatesNoOutputFolder()
        {
            var work = NewTempFolder();
            try
            {
                var output = Path.Combine(work, "out");
                RunExe(["extract", "--input", SmallDrive, "-p", "sdz9", "-o", output], maxWait: Quick).AssertExitCode(1, "extract -p sdz9");
                Assert.IsFalse(Directory.Exists(output), "a failed extract created its output folder");

                RunExe(["extract-partition-image", "--input", SmallDrive, "-p", "sdz9", "-o", output], maxWait: Quick).AssertExitCode(1, "extract-partition-image -p sdz9");
                Assert.IsFalse(Directory.Exists(output), "a failed extract-partition-image created its output folder");
            }
            finally { TryDeleteFolder(work); }
        }

        //--mount is validated before anything loads: a folder mount point must exist and be empty (gh review F51)
        [TestMethod, Timeout(TimeoutMs)]
        public void MountPointFolderMissingOrNotEmpty_ExitsOne()
        {
            var work = NewTempFolder();
            try
            {
                var missing = Path.Combine(work, "missing");
                var missingResult = RunExe(["mount", "--input", SmallDrive, "-m", missing, "--no-explorer"], maxWait: Quick).AssertExitCode(1, "mount at a missing folder");
                Assert.IsTrue(missingResult.Stderr.Contains("does not exist"), $"unexpected message:{Environment.NewLine}{missingResult.LogTail()}");

                var notEmpty = Path.Combine(work, "not-empty");
                Directory.CreateDirectory(notEmpty);
                File.WriteAllText(Path.Combine(notEmpty, "occupant.txt"), "x");
                var notEmptyResult = RunExe(["mount", "--input", SmallDrive, "-m", notEmpty, "--no-explorer"], maxWait: Quick).AssertExitCode(1, "mount at a non-empty folder");
                Assert.IsTrue(notEmptyResult.Stderr.Contains("is not empty"), $"unexpected message:{Environment.NewLine}{notEmptyResult.LogTail()}");
            }
            finally { TryDeleteFolder(work); }
        }

        //A drive letter that a live volume already answers at is refused, never displaced; a letter the user chose is not
        //swapped for another. The system drive always answers, so this never mounts anything.
        [TestMethod, Timeout(TimeoutMs)]
        public void MountPointTaken_ExitsFive()
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
            var result = RunExe(["mount", "--input", SmallDrive, "-m", systemDrive, "--no-explorer"], maxWait: Quick).AssertExitCode(5, $"mount at {systemDrive}");

            Assert.IsTrue(result.Stderr.Contains("already answers at this mount point"), $"unexpected message:{Environment.NewLine}{result.LogTail()}");
            AssertNoStackTrace(result);
        }
    }
}
