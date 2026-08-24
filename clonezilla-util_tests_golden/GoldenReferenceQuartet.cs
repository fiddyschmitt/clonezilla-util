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

        //reader parallelism for the content pass (see the note above Parallel.ForEach).
        //Measured (warm cache, 8 native workers), quartet total:  DOP 8 = 959 min,  DOP 16 = 733,
        //DOP 24 = 1,330. 16 is the knee. 24 (tried 2026-08-24/25, content clean) regressed every
        //codec - bzip2 472 -> 546 min, gz 33.6 -> 69, xz 184 -> 245, and zst 43 -> 470 (10.9x) with
        //13.7k slow-read completions in a day vs 3.3k for the whole DOP-16 run: past ~2 readers per
        //worker the extra readers do not feed the workers, they thrash the span cache (each reader
        //pulls its own spans, the working set outgrows the RAM budget, and evicted spans get
        //re-decoded - the L12 amplification effect). Do not raise this without re-measuring.
        const int VerifyDop = 16;

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

                var failures = new ConcurrentBag<Failure>();
                long verified = 0;
                var runStarted = DateTime.Now;

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
                            failures.Add(new Failure(FailureKind.Structural, partition, entry, "desktop.ini unexpectedly present (was the deliberate filter in MountedPartitionImage removed? Then verify its hash here instead)"));
                        }
                    }

                    //golden -> mounted: every golden file must exist with a matching hash.
                    //History of the DOP: this ran at 8 because DOP 16 once made the mount serve wrong bytes
                    //under load (32k mismatches incl. cross-file bleed). That was Lead L11 - the Dokan
                    //context GCHandle freed at Cleanup while reads were in flight - cured at the source in
                    //433a5f3 and guarded by ConcurrentBleedStress, which verifies at DOP 24 against 8 native
                    //workers on every codec. With the serving stack genuinely concurrent now (S3 single-
                    //flight cache, S5 8 workers), reader parallelism is what keeps those workers busy, so
                    //the DOP was raised to cut the ~9-10h bzip2 leg (2026-08-23). If this ever produces
                    //mismatches again it is a real serving bug, not a reason to lower the DOP.
                    Parallel.ForEach(expectPresent, new ParallelOptions { MaxDegreeOfParallelism = VerifyDop }, entry =>
                    {
                        var outcome = HashAndCompare(root, partition, entry);
                        if (outcome != null) failures.Add(outcome);
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
                            failures.Add(new Failure(FailureKind.Structural, partition, new GoldenEntry(relative, ""), "Unexpected extra file in mount"));
                        }
                    }

                    Console.WriteLine($"{partition}: {golden.Count:N0} golden entries verified; " +
                                      $"{failures.Count} cumulative failures.");
                }

                //RETRY PASS. A read error under load is not the same fault as wrong bytes: the 2026-08-18
                //bzip2 run "failed" with 191 Dokan read timeouts (0x800705AA) in five bursts and ZERO
                //mismatches, and nothing here could tell a stall from a broken file. So every read error
                //and missing file is retried once, sequentially, on the now-quiet mount:
                //  retries clean   => a transient stall (the mount was slow, not wrong)
                //  still failing   => a persistent fault on that exact file
                //Wrong-bytes results are never retried - a mismatch is a mismatch.
                var retryable = failures.Where(f => f.Kind is FailureKind.ReadError or FailureKind.Missing).ToList();
                var transient = new List<Failure>();
                var persistent = new List<Failure>();
                if (retryable.Count > 0)
                {
                    Console.WriteLine($"retrying {retryable.Count:N0} read error(s)/missing file(s) sequentially on the quiet mount...");
                    foreach (var f in retryable)
                    {
                        var again = HashAndCompare($@"L:\{f.Partition}", f.Partition, f.Entry);
                        if (again == null) transient.Add(f);
                        else persistent.Add(again with { FirstSeen = f.FirstSeen, RetryDetail = again.Detail });
                    }
                }
                var mismatches = failures.Where(f => f.Kind == FailureKind.Mismatch).ToList();
                var structural = failures.Where(f => f.Kind == FailureKind.Structural).ToList();

                //the full record always goes to a file (a 13-hour run must never be repeated just to see
                //failure #21), and the assert names the verdict, not just a count
                var reportFile = Path.Combine(Path.GetTempPath(),
                    $"GoldenReferenceQuartet-{Path.GetFileName(imagePath)}-failures.txt");
                var report = new List<string>
                {
                    $"GoldenReferenceQuartet {Path.GetFileName(imagePath)} - run started {runStarted:yyyy-MM-dd HH:mm:ss}, {verified:N0} files verified",
                    $"  MD5 mismatches (wrong bytes)              : {mismatches.Count:N0}",
                    $"  structural (missing-should-be / extras)   : {structural.Count:N0}",
                    $"  read errors/missing, PERSISTENT on retry  : {persistent.Count:N0}",
                    $"  read errors/missing, transient (retry ok) : {transient.Count:N0}",
                    ""
                };
                void Section(string title, IEnumerable<Failure> items)
                {
                    var list = items.ToList();
                    if (list.Count == 0) return;
                    report.Add($"=== {title} ({list.Count:N0}) ===");
                    report.AddRange(list.OrderBy(f => f.FirstSeen).Select(f => f.ToString()));
                    report.Add("");
                }
                Section("MD5 MISMATCHES", mismatches);
                Section("STRUCTURAL", structural);
                Section("PERSISTENT read errors / missing (still failing on sequential retry)", persistent);
                Section("TRANSIENT read errors / missing (retried clean - stalls, not faults)", transient);
                File.WriteAllLines(reportFile, report);

                //Verdict. Wrong bytes, structural problems and persistent read faults fail the test.
                //Transient stalls are reported loudly but do NOT fail a content-verification test whose
                //every byte checked out - they are a performance/environment finding, tracked separately.
                var hard = mismatches.Count + structural.Count + persistent.Count;
                if (hard > 0)
                {
                    var examples = string.Join(Environment.NewLine,
                        mismatches.Concat(structural).Concat(persistent).Take(20).Select(f => f.ToString()));
                    Assert.Fail($"{hard:N0} hard golden-reference failure(s) of {verified:N0} files verified "
                              + $"({mismatches.Count:N0} MD5 mismatches, {structural.Count:N0} structural, {persistent.Count:N0} persistent read errors; "
                              + $"plus {transient.Count:N0} transient stalls that retried clean). Full report: {reportFile}{Environment.NewLine}First examples:{Environment.NewLine}{examples}");
                }
                if (transient.Count > 0)
                {
                    Console.WriteLine($"PASSED on content, but {transient.Count:N0} read(s) stalled under load and had to be retried "
                                    + $"(all retried clean). Timestamps in {reportFile}; correlate with SLOWREAD lines in the exe log.");
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

        enum FailureKind { Mismatch, Missing, ReadError, Structural }

        //One verification failure with WHEN it happened - the timestamp is what turns a list of 191
        //errors into "five bursts, hours apart" without inferring it from directory adjacency
        record Failure(FailureKind Kind, string Partition, GoldenEntry Entry, string Detail)
        {
            public DateTime FirstSeen { get; init; } = DateTime.Now;
            public string? RetryDetail { get; init; }

            public override string ToString()
            {
                var kind = Kind switch
                {
                    FailureKind.Mismatch => "MD5 mismatch",
                    FailureKind.Missing => "Missing from mount",
                    FailureKind.ReadError => "Read error",
                    _ => "Structural"
                };
                var retry = RetryDetail != null ? $" | on retry: {RetryDetail}" : "";
                return $"{FirstSeen:HH:mm:ss}  {kind}: {Partition}\\{Entry.RelativePath}: {Detail}{retry}";
            }
        }

        //null = the file hashed correctly; otherwise the classified failure
        static Failure? HashAndCompare(string root, string partition, GoldenEntry entry)
        {
            var fullPath = $@"{root}\{entry.RelativePath}";
            try
            {
                using var fs = File.OpenRead(fullPath);
                var md5 = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
                return md5 == entry.Md5
                    ? null
                    : new Failure(FailureKind.Mismatch, partition, entry, $"expected {entry.Md5} got {md5}");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new Failure(FailureKind.Missing, partition, entry, ex.Message);
            }
            catch (Exception ex)
            {
                return new Failure(FailureKind.ReadError, partition, entry, ex.Message);
            }
        }

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
