using System.Diagnostics;
using System.Text;

namespace clonezilla_util_tests
{
    /// <summary>What one finished run of the exe under test returned and printed. stdout is the verb's output
    /// (`list` records; UTF-8, no BOM); stderr is the log.</summary>
    public sealed record ExeResult(int ExitCode, byte[] StdoutBytes, string Stderr)
    {
        //decoded as-is: a BOM, if the exe ever wrote one, stays visible as U+FEFF
        public string Stdout => Encoding.UTF8.GetString(StdoutBytes);

        /// <summary>stdout split into `list` records on <paramref name="separator"/> ("\n" by default, as `list`
        /// writes them); empty records dropped.</summary>
        public string[] Records(char separator = '\n') => Stdout.Split(separator, StringSplitOptions.RemoveEmptyEntries);

        public string LogTail(int lines = 40)
        {
            var all = Stderr.Split('\n');
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }

        public ExeResult AssertExitCode(int expected, string what)
        {
            Assert.AreEqual(expected, ExitCode, $"{what} exited with code {ExitCode}, expected {expected}. End of its log:{Environment.NewLine}{LogTail()}");
            return this;
        }

        public ExeResult AssertSucceeded(string what) => AssertExitCode(0, what);
    }

    /// <summary>
    /// Base for tests that run the exe under test to completion: list, extract, extract-partition-image, and verbs
    /// expected to fail fast. <see cref="RunExe(IEnumerable{string}, string?, TimeSpan?)"/> passes arguments through
    /// ProcessStartInfo.ArgumentList, so a path with spaces or a trailing backslash reaches the exe exactly as
    /// written. The running exe is registered on the test instance so [TestCleanup] can kill it: a [Timeout] abandons
    /// the test thread without running its finally blocks (gh review F52).
    /// </summary>
    public abstract class ExeTestBase
    {
        Process? running;

        protected ExeResult RunExe(IEnumerable<string> args, string? workingDirectory = null, TimeSpan? maxWait = null)
        {
            var psi = CreateStartInfo(workingDirectory);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            return Run(psi, maxWait);
        }

        /// <summary>For command lines written as one string (the older tests' style).</summary>
        protected ExeResult RunExe(string arguments, string? workingDirectory = null, TimeSpan? maxWait = null)
        {
            var psi = CreateStartInfo(workingDirectory);
            psi.Arguments = arguments;
            return Run(psi, maxWait);
        }

        static ProcessStartInfo CreateStartInfo(string? workingDirectory)
        {
            var psi = new ProcessStartInfo(Main.ExeUnderTest)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                //held open until the exe is gone: a mount verb that mounts after all must not find stdin at EOF (it then
                //serves until killed) - the bounded wait below turns that into a failure instead of a hang
                RedirectStandardInput = true,
            };
            //The exe writes its log to "logs\" under its working directory, so tests that pick one must pick a
            //throwaway folder - never the test assets.
            if (workingDirectory != null) psi.WorkingDirectory = workingDirectory;
            return psi;
        }

        ExeResult Run(ProcessStartInfo psi, TimeSpan? maxWait)
        {
            var process = Process.Start(psi) ?? throw new Exception($"Could not start {psi.FileName}");
            running = process;
            try
            {
                //drain both pipes concurrently so neither can fill up and stall the exe
                var log = process.StandardError.ReadToEndAsync();
                using var stdout = new MemoryStream();
                var output = process.StandardOutput.BaseStream.CopyToAsync(stdout);

                if (!process.WaitForExit(maxWait ?? Timeout.InfiniteTimeSpan))
                {
                    Kill(process);
                    Assert.Fail($"The exe did not exit within {maxWait}. End of its log:{Environment.NewLine}{Tail(log)}");
                }

                output.Wait();
                return new ExeResult(process.ExitCode, stdout.ToArray(), log.Result);
            }
            finally
            {
                //Unregister only our own process (after a timeout this thread runs on, abandoned), then make sure it is gone.
                Interlocked.CompareExchange(ref running, null, process);
                Kill(process);
                process.Dispose();
            }
        }

        static string Tail(Task<string> log)
        {
            if (!log.Wait(TimeSpan.FromSeconds(10))) return "(log unavailable)";
            var all = log.Result.Split('\n');
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - 40)));
        }

        static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(30_000);
                }
            }
            catch { }
        }

        [TestCleanup]
        public void KillLeakedExe()
        {
            var process = Interlocked.Exchange(ref running, null);
            if (process != null) Kill(process);
        }

        /// <summary>A new empty folder under %TEMP%, for outputs, cache folders and synthetic images.</summary>
        protected static string NewTempFolder() => Directory.CreateTempSubdirectory("clonezilla-util-tests-").FullName;

        /// <summary>Best-effort cleanup: a file still held open must not turn a passing test into a failure, or mask
        /// the assertion that actually failed.</summary>
        protected static void TryDeleteFolder(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        protected static void AssertFileMd5(string path, string expectedMd5)
        {
            Assert.IsTrue(File.Exists(path), $"missing {path}");
            Assert.AreEqual(expectedMd5, libCommon.Utility.CalculateMD5(path), $"wrong content: {path}");
        }

        /// <summary>Copies a folder's files (not subfolders) - enough for a Clonezilla image folder.</summary>
        protected static string CopyFolderFiles(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
            return destination;
        }
    }
}
