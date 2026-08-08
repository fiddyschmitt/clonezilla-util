using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;

namespace clonezilla_util_tests.Mount.AsFiles
{
    /// <summary>
    /// Guards against the L11 class of bug: the mount serving ONE FILE'S BYTES UNDER ANOTHER
    /// FILE'S NAME when many clients read simultaneously and some die mid-read. Root cause and
    /// cure are recorded in TEST_ANALYSIS.md ("L11 CAUSE FOUND, 2026-08-08"): the Dokan context
    /// GCHandle was freed at Cleanup while in-flight reads still carried its raw value, so the
    /// recycled slot resolved to a different file's stream. The cure frees at CloseFile only.
    ///
    /// This test reproduces the triggering conditions the way the diagnosis harness did:
    ///  1. many SEPARATE READER PROCESSES hammer the mount with scattered cold reads (deep
    ///     queueing against the 4 native workers), and are KILLED mid-read in bursts - a killed
    ///     client is what delivers Cleanup while its reads are still in flight. In-process
    ///     threads cannot reproduce this; the kill must tear down a real client's handle table.
    ///  2. afterwards the mount is verified from THIS process at DOP 24 (the parallelism that
    ///     originally exposed L11) against the golden MD5 lists.
    ///
    /// Any mismatch fails, annotated with the L11 signature when applicable: a wrong hash that
    /// equals the expected hash of a DIFFERENT golden file means cross-file bleed ("got ==
    /// content of X"); a wrong hash matching nothing means garbage. Reads that error under the
    /// stress are retried once sequentially afterwards (a timeout under load is environmental;
    /// wrong bytes never are) and only fail the test if they still cannot be read.
    ///
    /// The pre-cure build failed this recipe roughly every other run with hundreds of
    /// mismatches; the cured build ran 12/12 phases + 4 harness trials with zero.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class ConcurrentBleedStress
    {
        const string GoldenFolder = @"E:\clonezilla-util-test resources\golden reference";
        const string ImagesFolder = @"E:\clonezilla-util-test resources\clonezilla images";
        const string Partition = "sda2";    //the partition with the richest file population; every L11 sighting was here

        const int ReaderProcesses = 24;     //concurrent client processes per burst (vs 4 native workers => deep queueing)
        const int KillBursts = 3;
        const int BurstSeconds = 45;
        const int VerifyDop = 24;           //the DOP that originally exposed L11

        //dense = adjacent files (many files per 32 MB span - where cross-file bleed shows as
        //neighbour contamination); scattered = strided across the partition (every read a fresh
        //span - constant cache churn). Counts are per-codec because decode speed varies ~10x.
        [TestMethod, Timeout(90 * 60_000)]
        public void bzip2() => StressImage($@"{ImagesFolder}\2022-07-16-22-img_pb-devops1_bzip2", dense: 6000, scattered: 400);

        [TestMethod, Timeout(60 * 60_000)]
        public void gz() => StressImage($@"{ImagesFolder}\2022-07-17-16-img_pb-devops1_gz", dense: 8000, scattered: 800);

        [TestMethod, Timeout(90 * 60_000)]
        public void xz() => StressImage($@"{ImagesFolder}\2022-07-17-12-img_pb-devops1_xz", dense: 4000, scattered: 400);

        [TestMethod, Timeout(60 * 60_000)]
        public void zst() => StressImage($@"{ImagesFolder}\2022-07-16-22-img_pb-devops1_zst", dense: 8000, scattered: 800);

        static void StressImage(string imagePath, int dense, int scattered)
        {
            //a previous codec's killed mount releases L: asynchronously; binding to the dying
            //letter serves a vanishing tree (every read FileNotFound) - so first wait it out
            WaitForDriveGone(TimeSpan.FromSeconds(90));

            var psi = new ProcessStartInfo(Main.ExeUnderTest, $"""mount --input "{imagePath}" -m L:\""")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                //the mount verb exits (code 0) on stdin EOF - the shell harnesses pipe
                //`tail -f /dev/null` into it for the same reason. Redirect stdin and simply
                //never close it, so the mount stays up until we Kill it.
                RedirectStandardInput = true
            };
            var mountingComplete = new ManualResetEventSlim(false);
            var mount = Process.Start(psi)!;
            //drain stdout continuously (a full pipe would block the mount) and watch for the
            //product's own readiness signal - Directory.Exists alone is NOT readiness
            mount.OutputDataReceived += (_, e) => { if (e.Data?.Contains("Mounting complete") == true) mountingComplete.Set(); };
            mount.ErrorDataReceived += (_, _) => { };
            mount.BeginOutputReadLine();
            mount.BeginErrorReadLine();
            var readers = new List<Process>();

            try
            {
                WaitForMount(mount, mountingComplete, TimeSpan.FromMinutes(30));

                var root = $@"L:\{Partition}";
                var golden = LoadGoldenList($@"{GoldenFolder}\golden-{Partition}.md5")
                    .Where(e => !Path.GetFileName(e.RelativePath).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                //prove the mount actually serves files before unleashing the stress
                ProbeRead($@"{root}\{golden[0].RelativePath}", TimeSpan.FromSeconds(60));

                //-------- phase 1: kill-bursts of external reader processes --------
                for (var burst = 1; burst <= KillBursts; burst++)
                {
                    for (var r = 0; r < ReaderProcesses; r++)
                    {
                        readers.Add(StartReaderProcess(root, golden, seed: burst * 1000 + r));
                        //stagger the spawns: 24 simultaneous process creations starved the
                        //vstest runner<->testhost channel and produced a spurious
                        //"Test host process crashed" abort while the host kept running
                        Thread.Sleep(125);
                    }

                    Thread.Sleep(TimeSpan.FromSeconds(BurstSeconds));

                    //THE trigger: tear down live clients while their reads are still in flight
                    foreach (var reader in readers)
                    {
                        try { reader.Kill(entireProcessTree: true); } catch { }
                    }
                    foreach (var reader in readers)
                    {
                        try { reader.WaitForExit(5000); } catch { }
                        reader.Dispose();
                    }
                    readers.Clear();
                    Console.WriteLine($"burst {burst}/{KillBursts}: {ReaderProcesses} readers killed mid-read");
                    Thread.Sleep(3000);
                }

                //-------- phase 2: DOP-24 content verification against the golden list --------
                var detectionSet = new List<GoldenEntry>(dense + scattered);
                detectionSet.AddRange(golden.Take(dense));                                  //dense adjacent
                var stride = Math.Max(1, golden.Count / scattered);
                for (var i = dense; i < golden.Count && detectionSet.Count < dense + scattered; i += stride)
                {
                    detectionSet.Add(golden[i]);                                            //scattered
                }

                //reverse map for the L11 signature: does a wrong hash match ANOTHER file's content?
                var donorByMd5 = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in golden)
                {
                    donorByMd5.TryAdd(e.Md5, e.RelativePath);
                }

                var mismatches = new ConcurrentBag<string>();
                var readErrors = new ConcurrentBag<GoldenEntry>();

                Parallel.ForEach(detectionSet, new ParallelOptions { MaxDegreeOfParallelism = VerifyDop }, entry =>
                {
                    var outcome = HashAndCompare(root, entry, donorByMd5);
                    if (outcome.Mismatch != null) mismatches.Add(outcome.Mismatch);
                    else if (outcome.Errored) readErrors.Add(entry);
                });

                //read errors under DOP-24 stress can be timeouts (environmental); wrong bytes
                //cannot. Retry stragglers sequentially on a now-quiet mount before judging.
                var persistentErrors = new List<string>();
                foreach (var entry in readErrors)
                {
                    var retry = HashAndCompare(root, entry, donorByMd5);
                    if (retry.Mismatch != null) mismatches.Add(retry.Mismatch);
                    else if (retry.Errored) persistentErrors.Add($"{entry.RelativePath}: {retry.Error}");
                }

                Console.WriteLine($"verified {detectionSet.Count:N0} files at DOP {VerifyDop} " +
                                  $"({dense:N0} dense + {scattered:N0} scattered); " +
                                  $"{readErrors.Count} transient errors retried, {persistentErrors.Count} persistent.");

                if (!mismatches.IsEmpty || persistentErrors.Count > 0)
                {
                    var all = mismatches.Concat(persistentErrors.Select(e => $"Unreadable after retry: {e}")).ToList();
                    var examples = string.Join(Environment.NewLine, all.Take(20));
                    Assert.Fail($"{all.Count:N0} concurrent-serving failures (any MD5 mismatch = the L11 bug class):{Environment.NewLine}{examples}");
                }
            }
            finally
            {
                foreach (var reader in readers)
                {
                    try { reader.Kill(entireProcessTree: true); } catch { }
                    reader.Dispose();
                }
                try
                {
                    mount?.Kill();
                    mount?.WaitForExit();
                }
                catch
                {
                    //the process may already have exited
                }
                //let Dokan release L: before the next codec mounts (see WaitForDriveGone)
                WaitForDriveGone(TimeSpan.FromSeconds(90));
            }
        }

        static void WaitForDriveGone(TimeSpan maxWait)
        {
            var waited = Stopwatch.StartNew();
            while (Directory.Exists($@"L:\{Partition}") && waited.Elapsed < maxWait)
            {
                Thread.Sleep(1000);
            }
            //not fatal if it lingers - the "Mounting complete" gate protects the next mount -
            //but note it, because a stuck letter usually means an orphaned mount process
            if (Directory.Exists($@"L:\{Partition}"))
            {
                Console.WriteLine($@"warning: L:\{Partition} still present after {maxWait} - stale mount?");
            }
        }

        static void ProbeRead(string path, TimeSpan maxWait)
        {
            var waited = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    var buffer = new byte[4096];
                    fs.Read(buffer, 0, buffer.Length);
                    return;
                }
                catch (Exception ex)
                {
                    if (waited.Elapsed > maxWait)
                    {
                        Assert.Fail($"Mount reported complete but cannot serve '{path}' after {maxWait}: {ex.Message}");
                    }
                    Thread.Sleep(2000);
                }
            }
        }

        record HashOutcome(string? Mismatch, bool Errored, string? Error);

        static HashOutcome HashAndCompare(string root, GoldenEntry entry, Dictionary<string, string> donorByMd5)
        {
            try
            {
                using var fs = File.OpenRead($@"{root}\{entry.RelativePath}");
                var md5 = Convert.ToHexString(MD5.HashData(fs)).ToLowerInvariant();
                if (md5 == entry.Md5) return new HashOutcome(null, false, null);

                var signature = donorByMd5.TryGetValue(md5, out var donor) && donor != entry.RelativePath
                    ? $"[got == content of: {donor}] <= CROSS-FILE BLEED"
                    : "[got matches no golden file]";
                return new HashOutcome($"MD5 mismatch: {entry.RelativePath} expected {entry.Md5} got {md5} {signature}", false, null);
            }
            catch (Exception ex)
            {
                return new HashOutcome(null, true, ex.Message);
            }
        }

        //A reader is a genuinely separate client process so that killing it tears down a real
        //handle table with reads in flight. It walks its own strided slice of the golden list,
        //reading the first 8 MB of each file (a fresh cold span per file keeps reads SLOW, which
        //is what leaves them in flight at kill time), and loops until killed.
        static Process StartReaderProcess(string root, List<GoldenEntry> golden, int seed)
        {
            var stride = 337;   //co-prime-ish stride scatters each reader differently across the partition
            var start = seed % stride;
            var listFile = Path.Combine(Path.GetTempPath(), $"bleed-reader-{Environment.ProcessId}-{seed}.txt");
            var files = new List<string>(256);
            for (var i = start; i < golden.Count && files.Count < 256; i += stride)
            {
                files.Add($@"{root}\{golden[i].RelativePath}");
            }
            File.WriteAllLines(listFile, files);

            var script =
                $"$b = New-Object byte[] 262144; " +
                $"while ($true) {{ foreach ($f in (Get-Content -LiteralPath '{listFile}')) {{ " +
                $"try {{ $s = [IO.File]::OpenRead($f); while ($s.Read($b, 0, $b.Length) -gt 0 -and $s.Position -lt 8MB) {{ }}; $s.Close() }} catch {{ }} }} }}";

            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                //own the children's stdio: with inherited handles, 24 readers grabbing
                //testhost's stdout/stderr pipes made vstest declare the host crashed. The
                //script writes nothing, so the unread pipes can never fill and block.
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var p = Process.Start(psi)!;
            try
            {
                //the readers' job is to keep the MOUNT saturated (IO-bound); they must never
                //out-schedule the test host's own threads or vstest's heartbeat
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch { /* the process may already have exited */ }
            return p;
        }

        static void WaitForMount(Process? process, ManualResetEventSlim mountingComplete, TimeSpan maxWait)
        {
            var waited = Stopwatch.StartNew();
            while (true)
            {
                if (process?.HasExited ?? true)
                {
                    Assert.Fail($"The exe under test exited (code {(process != null ? process.ExitCode.ToString() : "unknown")}) before the mount appeared.");
                }
                //readiness = the product's own "Mounting complete" line AND the tree visible.
                //Directory.Exists alone can bind to the PREVIOUS test's dying mount.
                if (mountingComplete.IsSet && Directory.Exists($@"L:\{Partition}"))
                {
                    return;
                }
                if (waited.Elapsed > maxWait)
                {
                    Assert.Fail($@"Timed out after {waited.Elapsed} waiting for 'Mounting complete' + L:\{Partition}.");
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
}
