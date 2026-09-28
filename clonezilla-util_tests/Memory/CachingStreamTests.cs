using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using libCommon.Memory;
using libCommon.Streams;

namespace clonezilla_util_tests.Memory
{
    // Correctness + memory-balance tests for CachingStream on native slots. Every assertion is keyed to the
    // injected per-test governor's LiveNativeBytes, so it is isolated from other test classes running in
    // parallel. Served bytes are always verified against the source's deterministic content.
    [TestClass]
    public class CachingStreamTests
    {
        const int Span = FakePositionalSource.SpanSize;   // 64 KB
        static MemoryGovernor Gov(long capSpans) =>
            new MemoryGovernor(new FakeSampler(100L << 30).Sample, reserveBytes: 1L << 20, cacheCapBytes: capSpans * Span, autoTick: false);

        static void VerifyConcurrent(CachingStream s, long pos, int count)
        {
            var buf = new byte[count];
            int n = s.ReadAt(pos, buf, 0, count);
            for (int i = 0; i < n; i++)
                Assert.AreEqual(FakePositionalSource.ByteAt(pos + i), buf[i], $"wrong byte at {pos + i}");
        }

        [TestMethod]
        public void ServedBytesAlwaysEqualSource_ParallelReaders()
        {
            var g = Gov(capSpans: 4);   // tiny cap -> constant eviction
            long len = 8L * 1024 * 1024;
            var src1 = new FakePositionalSource(len);
            var src2 = new FakePositionalSource(len);
            using var s1 = new CachingStream(src1, src1, "s1", g);
            using var s2 = new CachingStream(src2, src2, "s2", g);

            var threads = new Thread[16];
            Exception? failure = null;
            for (int t = 0; t < threads.Length; t++)
            {
                bool suppress = t % 2 == 0;   // half hold a "worker" (decode-and-drop path)
                int seed = t;
                threads[t] = new Thread(() =>
                {
                    CachingStream.SuppressCoalesceWait = suppress;
                    var rnd = new Random(seed);
                    var buf = new byte[200 * 1024];
                    try
                    {
                        for (int it = 0; it < 4000; it++)
                        {
                            var s = rnd.Next(2) == 0 ? s1 : s2;
                            long pos = (long)(rnd.NextDouble() * (len - 1));
                            int count = 1 + rnd.Next(buf.Length);
                            if (pos + count > len) count = (int)(len - pos);
                            int n = s.ReadAt(pos, buf, 0, count);
                            for (int i = 0; i < n; i++)
                                if (buf[i] != FakePositionalSource.ByteAt(pos + i)) throw new Exception($"wrong byte at {pos + i}");
                        }
                    }
                    catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                });
            }
            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join();

            Assert.IsNull(failure, failure?.ToString());
            s1.Dispose(); s2.Dispose();
            Assert.AreEqual(0, g.LiveNativeBytes, "every native block should be freed after dispose");
        }

        [TestMethod]
        public void NewestSlotSurvives_SpanLargerThanCap()
        {
            // cap is smaller than a single span: the span can't be cached "within budget", but the
            // never-block/newest-slot rule must still serve it correctly.
            var g = new MemoryGovernor(new FakeSampler(100L << 30).Sample, 1L << 20, Span / 2, autoTick: false);
            var src = new FakePositionalSource(1L << 20);
            using var s = new CachingStream(src, src, "s", g);
            VerifyConcurrent(s, 0, Span);
            VerifyConcurrent(s, 0, Span);   // again: hit or re-decode, still correct
        }

        [TestMethod]
        public void NativeAllocFreeBalance_UnderInjectedFailures()
        {
            var g = Gov(capSpans: 4);
            long len = 2L << 20;
            var src = new FakePositionalSource(len) { ThrowEveryNth = 3 };
            using (var s = new CachingStream(src, src, "s", g))
            {
                var buf = new byte[Span];
                for (int i = 0; i < 300; i++)
                {
                    long pos = (long)(i * Span) % (len - Span);
                    try { s.ReadAt(pos, buf, 0, buf.Length); } catch (System.IO.IOException) { /* injected */ }
                }
            }
            Assert.AreEqual(0, g.LiveNativeBytes, "reservations must balance even when decodes fail");
        }

        [TestMethod]
        public void ShortDecode_Throws_AndIsNotCachedAsEof()
        {
            // A span that decodes short (its source ended early) must fail the read and cache nothing. A cached
            // truncated slot would answer every later read of the span's tail with end-of-file (review F15).
            var g = Gov(capSpans: 64);
            var src = new FakePositionalSource(4L * Span) { ShortSpanStart = Span, ShortBy = 1000 };
            using (var s = new CachingStream(src, src, "short", g))
            {
                var buf = new byte[50];
                bool threw = false;
                try { s.ReadAt(Span + 10, buf, 0, buf.Length); } catch (System.IO.IOException) { threw = true; }
                Assert.IsTrue(threw, "a short decode should throw");

                src.ShortSpanStart = -1;   // the source recovers: the retry must decode afresh and serve the whole span
                long tail = 2L * Span - buf.Length;
                Assert.AreEqual(buf.Length, s.ReadAt(tail, buf, 0, buf.Length), "the span's tail was served as end-of-file");
                for (int i = 0; i < buf.Length; i++) Assert.AreEqual(FakePositionalSource.ByteAt(tail + i), buf[i]);
            }
            Assert.AreEqual(0, g.LiveNativeBytes, "the failed decode's reservation must be released");
        }

        [TestMethod]
        public void LegacyPath_ReadPastEof_ReturnsZero()
        {
            // With no read suggestor the legacy span is clamped to Length, which past EOF made it negative: the read
            // threw from the buffer pool instead of returning 0 (review F15).
            var g = Gov(capSpans: 8);
            var src = new FakeSeekableSource(100_000);
            using var s = new CachingStream(src, null, "legacy-eof", g);
            var buf = new byte[10];
            Assert.AreEqual(0, s.ReadAt(100_000, buf, 0, buf.Length));
            Assert.AreEqual(0, s.ReadAt(250_000, buf, 0, buf.Length));
            Assert.AreEqual(10, s.ReadAt(99_990, buf, 0, buf.Length));
            for (int i = 0; i < buf.Length; i++) Assert.AreEqual(FakePositionalSource.ByteAt(99_990 + i), buf[i]);
            Assert.AreEqual(s.CachedBytes, g.LiveNativeBytes, "the EOF reads must not leave a reservation behind");
        }

        [TestMethod]
        public void DisposeDuringReads_ThrowsObjectDisposed_AndFreesBlocks()
        {
            var g = Gov(capSpans: 8);
            var src = new FakePositionalSource(1L << 20);
            var s = new CachingStream(src, src, "s", g);
            var buf = new byte[Span];
            s.ReadAt(0, buf, 0, buf.Length);
            s.Dispose();

            bool threw = false;
            try { s.ReadAt(0, buf, 0, buf.Length); } catch (ObjectDisposedException) { threw = true; }
            Assert.IsTrue(threw, "ReadAt after dispose should throw ObjectDisposedException");
            Assert.AreEqual(0, g.LiveNativeBytes);
        }

        [TestMethod]
        public void AbandonedInstance_FinalizerFreesBlocks()
        {
            var g = new MemoryGovernor(new FakeSampler(100L << 30).Sample, 1L << 20, 100L * Span, autoTick: false);
            CreateAndAbandon(g);

            GC.Collect(); GC.WaitForPendingFinalizers();
            GC.Collect(); GC.WaitForPendingFinalizers();

            Assert.AreEqual(0, g.LiveNativeBytes, "the NativeBlock finalizer must free an abandoned stream's blocks");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void CreateAndAbandon(MemoryGovernor g)
        {
            var src = new FakePositionalSource(1L << 20);
            var s = new CachingStream(src, src, "abandoned", g);   // deliberately never disposed
            var buf = new byte[Span];
            for (int i = 0; i < 8; i++) s.ReadAt((long)i * Span, buf, 0, buf.Length);
        }

        [TestMethod]
        public void LegacyPath_ServedBytesEqualSource_ParallelReaders()
        {
            var g = Gov(capSpans: 4);
            long len = 4L * 1024 * 1024;
            var src = new FakeSeekableSource(len);   // NOT IPositionalReader -> legacy path
            using var s = new CachingStream(src, src, "legacy", g);

            Exception? failure = null;
            var threads = new Thread[8];
            for (int t = 0; t < threads.Length; t++)
            {
                int seed = t;
                threads[t] = new Thread(() =>
                {
                    var rnd = new Random(seed);
                    var buf = new byte[100 * 1024];
                    try
                    {
                        for (int it = 0; it < 2000; it++)
                        {
                            long pos = (long)(rnd.NextDouble() * (len - 1));
                            int count = 1 + rnd.Next(buf.Length);
                            if (pos + count > len) count = (int)(len - pos);
                            int n = s.ReadAt(pos, buf, 0, count);
                            for (int i = 0; i < n; i++)
                                if (buf[i] != FakePositionalSource.ByteAt(pos + i)) throw new Exception($"wrong byte at {pos + i}");
                        }
                    }
                    catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                });
            }
            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join();

            Assert.IsNull(failure, failure?.ToString());
            s.Dispose();
            Assert.AreEqual(0, g.LiveNativeBytes);
        }

        [TestMethod]
        public void LegacyPath_HitDoesNotWaitForASlowDecode()
        {
            // Proves the listLock/decodeLock split: a cache hit must return while another thread holds
            // decodeLock inside a slow decode.
            var g = Gov(capSpans: 64);
            var src = new FakeSeekableSource(2L << 20);
            using var s = new CachingStream(src, src, "legacy", g);

            var warm = new byte[Span];
            s.ReadAt(0, warm, 0, warm.Length);        // span 0 now cached

            src.DecodeDelayMs = 500;
            src.EnteredDecode = new ManualResetEventSlim(false);
            var aDone = new ManualResetEventSlim(false);
            var tA = new Thread(() => { var b = new byte[Span]; s.ReadAt(Span, b, 0, b.Length); aDone.Set(); });
            tA.Start();

            Assert.IsTrue(src.EnteredDecode.Wait(5000), "the slow decode should have started");
            var sw = Stopwatch.StartNew();
            int n = s.ReadAt(0, warm, 0, warm.Length);   // HIT on span 0
            sw.Stop();

            Assert.IsFalse(aDone.IsSet, "the slow decoder should still be running");
            Assert.IsTrue(sw.ElapsedMilliseconds < 250, $"the hit waited {sw.ElapsedMilliseconds} ms behind the decode");
            for (int i = 0; i < n; i++) Assert.AreEqual(FakePositionalSource.ByteAt(i), warm[i]);
            tA.Join();
        }
    }
}
