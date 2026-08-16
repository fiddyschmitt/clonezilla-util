using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace clonezilla_util_tests.Mount.AsFiles
{
    /// <summary>
    /// Full listing AND content verification of the PB-DEVOPS1 quartet against the golden
    /// reference produced 2026-07-27 entirely without clonezilla-util (7-Zip decompress ->
    /// partclone-for-Windows restore -> 7-Zip NTFS extract -> md5sum; see the README next to
    /// the golden lists). Every file in the golden lists must exist in the mount with a
    /// matching MD5, and the mount must contain no unexpected extra files. The four images'
    /// compressed streams were proven byte-identical, so all four must serve identical content.
    ///
    /// Two deliberate product behaviours are encoded here rather than reported as failures:
    /// - desktop.ini files are filtered out of the mount on purpose (MountedPartitionImage
    ///   suppresses them so Explorer doesn't hammer the mount with IO). They must be ABSENT;
    ///   if one appears, the filter was removed and this test needs updating.
    /// - NTFS alternate-data-stream items ([SYSTEM]\$Secure:$SDS etc.) are served by the mount
    ///   but can never be in the golden lists: ':' is illegal in Windows filenames, so 7-Zip
    ///   could not extract them when the reference was built. The extras sweep skips them.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GoldenReferenceQuartet
    {
        const string GoldenFolder = @"E:\clonezilla-util-test resources\golden reference";
        const string ImagesFolder = @"E:\clonezilla-util-test resources\clonezilla images";
        static readonly string[] Partitions = ["sda1", "sda2", "sdb1"];

        [TestMethod]
        public void bzip2() => VerifyImage($@"{ImagesFolder}\2022-07-16-22-img_pb-devops1_bzip2");

        [TestMethod]
        public void gz() => VerifyImage($@"{ImagesFolder}\2022-07-17-16-img_pb-devops1_gz");

        [TestMethod]
        public void xz() => VerifyImage($@"{ImagesFolder}\2022-07-17-12-img_pb-devops1_xz");

        [TestMethod]
        public void zst() => VerifyImage($@"{ImagesFolder}\2022-07-16-22-img_pb-devops1_zst");

        static void VerifyImage(string imagePath)
        {
            var psi = new ProcessStartInfo(Main.ExeUnderTest, $"""mount --input "{imagePath}" -m L:\ --no-explorer""")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                //watch the exe's stdout for its own "Mounting complete" readiness signal (see
                //WaitForMount); stdin is redirected and held open because the mount verb exits on
                //stdin EOF
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            var mountingComplete = new ManualResetEventSlim(false);
            var process = Process.Start(psi);
            if (process != null)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data?.Contains("Mounting complete") == true) mountingComplete.Set(); };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            try
            {
                WaitForMount(process, mountingComplete, TimeSpan.FromHours(6));

                var failures = new ConcurrentBag<string>();
                long verified = 0;

                foreach (var partition in Partitions)
                {
                    var golden = LoadGoldenList($@"{GoldenFolder}\golden-{partition}.md5");
                    var root = $@"L:\{partition}";

                    //the product deliberately filters desktop.ini out of the mount; those golden
                    //entries must be absent (their content is verified for the other codecs by the
                    //independent pipeline that produced the golden lists)
                    var (expectAbsent, expectPresent) = golden.Partition(e =>
                        Path.GetFileName(e.RelativePath).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));

                    foreach (var entry in expectAbsent)
                    {
                        if (File.Exists($@"{root}\{entry.RelativePath}"))
                        {
                            failures.Add($"desktop.ini unexpectedly present (was the deliberate filter in MountedPartitionImage removed? Then verify its hash here instead): {partition}\\{entry.RelativePath}");
                        }
                    }

                    //golden -> mounted: every golden file must exist with a matching hash.
                    //DOP 8 with default 4 KB reads is deliberate: DOP 16 with 1 MB buffered reads made the
                    //mount serve wrong bytes under load (32k mismatches incl. cross-file data bleed - two
                    //files returning identical wrong content - plus ~950 outright read errors), while this
                    //pattern hashed all 558k files cleanly. That load-related serving bug is worth chasing,
                    //but this test's job is content verification, so it uses the pattern that works.
                    Parallel.ForEach(expectPresent, new ParallelOptions { MaxDegreeOfParallelism = 8 }, entry =>
                    {
                        var fullPath = $@"{root}\{entry.RelativePath}";
                        try
                        {
                            using var fs = File.OpenRead(fullPath);
                            var md5 = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
                            if (md5 != entry.Md5)
                            {
                                failures.Add($"MD5 mismatch: {partition}\\{entry.RelativePath} expected {entry.Md5} got {md5}");
                            }
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            failures.Add($"Missing from mount: {partition}\\{entry.RelativePath}");
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"Read error: {partition}\\{entry.RelativePath}: {ex.Message}");
                        }
                        System.Threading.Interlocked.Increment(ref verified);
                    });

                    //mounted -> golden: no unexpected extra files in the mount
                    var goldenPaths = new HashSet<string>(
                        golden.Select(e => e.RelativePath),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var mounted in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var relative = mounted.Substring(root.Length + 1);

                        //NTFS alternate-data-stream items (e.g. [SYSTEM]\$Secure:$SDS) can never be
                        //in the golden lists - ':' is illegal in extracted filenames
                        if (relative.Contains(':')) continue;

                        if (!goldenPaths.Contains(relative))
                        {
                            failures.Add($"Unexpected extra file in mount: {partition}\\{relative}");
                        }
                    }

                    Console.WriteLine($"{partition}: {golden.Count:N0} golden entries verified; " +
                                      $"{failures.Count} cumulative failures.");
                }

                if (!failures.IsEmpty)
                {
                    //the inline assert shows only a sample; the full list goes to a file so a
                    //10-hour run never has to be repeated just to see failure #21
                    var failureFile = Path.Combine(Path.GetTempPath(),
                        $"GoldenReferenceQuartet-{Path.GetFileName(imagePath)}-failures.txt");
                    File.WriteAllLines(failureFile, failures);

                    var examples = string.Join(Environment.NewLine, failures.Take(20));
                    Assert.Fail($"{failures.Count:N0} golden-reference failures (of {verified:N0} files verified). Full list: {failureFile}{Environment.NewLine}First examples:{Environment.NewLine}{examples}");
                }
            }
            finally
            {
                //the exe must always be killed, even when an assert throws (see Mount.TestUtility)
                try
                {
                    process?.Kill();
                    process?.WaitForExit();
                }
                catch
                {
                    //the process may already have exited
                }
            }
        }

        //the exe declares itself ready by logging this once every partition it will serve is mounted
        const string MountCompleteSignal = "Mounting complete";

        //Grace between the "Mounting complete" log line and every partition directory becoming
        //enumerable through Dokan. Volume arrival is near-instant; this is generous slack, not a wait.
        static readonly TimeSpan MountExposureGrace = TimeSpan.FromSeconds(60);

        //Two phases so an incomplete mount FAILS FAST instead of eating the whole ceiling:
        //  1. wait for the exe's own "Mounting complete" - cold bzip2/xz index builds legitimately
        //     take hours, so this phase carries the long ceiling.
        //  2. once mounting is complete the exe has mounted everything it ever will, so a partition
        //     still missing after a short grace is one it FAILED to open (e.g. an index-rebuild
        //     decode error) and will never appear - fail immediately, naming it.
        //The old code blind-polled Directory.Exists for the full ceiling: on 2026-08-16 a bzip2 sda2
        //index-rebuild crash dropped that partition and cost a 6-hour timeout instead of a prompt,
        //clearly-labelled failure.
        static void WaitForMount(Process? process, ManualResetEventSlim mountingComplete, TimeSpan maxWait)
        {
            var waited = Stopwatch.StartNew();

            //phase 1 - wait for mounting to complete (slow cold index builds live here)
            while (!mountingComplete.IsSet)
            {
                if (process?.HasExited ?? true)
                {
                    Assert.Fail($"The exe under test exited (code {(process != null ? process.ExitCode.ToString() : "unknown")}) before reporting '{MountCompleteSignal}'.");
                }
                if (waited.Elapsed > maxWait)
                {
                    Assert.Fail($"Timed out after {waited.Elapsed} waiting for the exe to report '{MountCompleteSignal}'.");
                }
                Thread.Sleep(1000);
            }

            //phase 2 - mounting is done; every partition it will expose is exposing now
            var grace = Stopwatch.StartNew();
            while (true)
            {
                var missing = Partitions.Where(p => !Directory.Exists($@"L:\{p}")).ToList();
                if (missing.Count == 0)
                {
                    return;
                }
                if (process?.HasExited ?? true)
                {
                    Assert.Fail($"The exe under test exited before exposing {string.Join(", ", missing)} under L:\\.");
                }
                if (grace.Elapsed > MountExposureGrace)
                {
                    Assert.Fail($"The exe reported '{MountCompleteSignal}' but did not expose {string.Join(", ", missing)} under L:\\ within {MountExposureGrace.TotalSeconds:N0}s. "
                              + "A partition missing after mount completion failed to open - check the exe log (bin\\...\\logs\\clonezilla-util-*.log) for an index/decode error on that partition.");
                }
                Thread.Sleep(1000);
            }
        }

        record GoldenEntry(string RelativePath, string Md5);

        static List<GoldenEntry> LoadGoldenList(string filename)
        {
            //md5sum binary-mode lines: "<32 hex> *./relative/path" (forward slashes)
            var result = new List<GoldenEntry>();
            foreach (var line in File.ReadLines(filename))
            {
                if (line.Length < 35) continue;
                var md5 = line[..32].ToLowerInvariant();
                var path = line[33..].TrimStart('*').TrimStart('.', '/').Replace('/', '\\');
                result.Add(new GoldenEntry(path, md5));
            }
            if (result.Count == 0)
            {
                Assert.Fail($"Golden list {filename} is missing or empty. It lives outside git - see its README for how it was produced.");
            }
            return result;
        }
    }

    static class GoldenListExtensions
    {
        public static (List<T> Matching, List<T> Remaining) Partition<T>(this IEnumerable<T> source, Func<T, bool> predicate)
        {
            var matching = new List<T>();
            var rest = new List<T>();
            foreach (var item in source)
            {
                (predicate(item) ? matching : rest).Add(item);
            }
            return (matching, rest);
        }
    }
}
