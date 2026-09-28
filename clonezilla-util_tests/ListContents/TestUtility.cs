using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace clonezilla_util_tests.ListContents
{
    public static class TestUtility
    {
        //The running `list` exe, registered so ListTestBase's [TestCleanup] can kill it. A [Timeout] expiry
        //abandons the test thread WITHOUT running its finally blocks, so only TestCleanup can reclaim the exe
        //(and any mount it holds) - the same pattern as ConcurrentBleedStress (gh review F52).
        static Process? current;

        public static void ConfirmContainsStrings(string exeUnderTest, string args, IList<string> expectedStrings)
        {
            var psi = new ProcessStartInfo(exeUnderTest, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,   //list writes UTF-8 records to stdout
            };

            var process = Process.Start(psi) ?? throw new Exception($"Could not start {exeUnderTest}");
            current = process;
            try
            {
                //the log goes to stderr; drain it concurrently so neither pipe can fill up and stall the exe
                var log = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                //a list that crashes after printing the expected lines must not pass
                Assert.AreEqual(0, process.ExitCode, $"list exited with code {process.ExitCode}. End of its log:{Environment.NewLine}{Tail(log.Result, 40)}");

                expectedStrings
                    .ToList()
                    .ForEach(expectedString =>
                    {
                        var contains = output.Contains(expectedString);
                        Assert.IsTrue(contains, $"Could not find: {expectedString}");
                    });
            }
            finally
            {
                //Kill only OUR process, and unregister it only if it is still the registered one: after a
                //timeout this thread runs on, abandoned, and must not touch the next test's exe.
                Interlocked.CompareExchange(ref current, null, process);
                Kill(process);
                process.Dispose();
            }
        }

        /// <summary>Kills the registered `list` exe, if any. Called from [TestCleanup].</summary>
        public static void KillCurrent()
        {
            var process = Interlocked.Exchange(ref current, null);
            if (process != null) Kill(process);
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

        static string Tail(string text, int lines)
        {
            var all = text.Split('\n');
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
    }

    /// <summary>Base for the list integration tests: reclaims a `list` exe that a [Timeout] left running.</summary>
    public abstract class ListTestBase
    {
        [TestCleanup]
        public void KillLeakedList() => TestUtility.KillCurrent();
    }
}
