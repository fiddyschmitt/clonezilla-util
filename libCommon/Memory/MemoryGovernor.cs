using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Serilog;

namespace libCommon.Memory
{
    /// <summary>A cache that lets the governor account and shed its memory. Implemented by CachingStream.</summary>
    public interface IGovernedCache
    {
        string Name { get; }
        long CachedBytes { get; }
        int SlotCount { get; }
        /// <summary>Tick of the least-recently-touched slot (Environment.TickCount64); long.MaxValue when empty.</summary>
        long OldestTouchTick { get; }
        /// <summary>Evict LRU-tail slots until at least <paramref name="wantBytes"/> is freed or the instance
        /// hits its floor. Takes ONLY this instance's own lock. Returns false if nothing was evictable.</summary>
        bool TryEvict(long wantBytes, out long freedBytes);
    }

    /// <summary>
    /// Process-wide memory manager for the decoded-span caches. Grows the caches until the machine's
    /// AVAILABLE physical memory falls to a reserve, and sheds globally-oldest spans when anything else
    /// (7-Zip MFT parses, another process, a second mount) needs memory. Replaces the old "¼ of total RAM,
    /// split evenly per partition" budget, which ignored what was actually free and never shrank.
    ///
    /// Lock ordering: <c>registryLock &gt; any IGovernedCache instance lock</c>. The governor takes an
    /// instance lock only inside TryEvict, and only while holding registryLock. An instance never calls a
    /// governor method that can take registryLock (Admit/Register/Unregister/Tick) while holding its own
    /// lock; only the Interlocked counters (Release/CountHit/...) are safe under an instance lock.
    /// </summary>
    public sealed class MemoryGovernor
    {
        public const int FloorSlots = 2;    // every instance keeps at least this many spans (prevents thrash-to-zero)
        const long StaleMs = 3000;          // a sample older than this is refreshed before an admit decision
        const long EvictBatchBytes = 64L * 1024 * 1024;

        readonly Func<long, MemorySample?> sampler;
        long reserveBytes;
        long cacheCapBytes;
        string configSource = "default (8% RAM)";
        bool measured = true;

        long liveNativeBytes;               // reserved-but-not-yet-freed native cache bytes (Interlocked)
        volatile MemorySample lastSample;

        readonly object registryLock = new();
        readonly List<WeakReference<IGovernedCache>> registry = new();
        Timer? timer;
        readonly bool autoTick = true;   //false in tests so Tick() is driven deterministically

        // report counters (Interlocked), snapshotted and reset each emitted line
        long hits, misses, redecodes, evictions, evictedBytes, overReserveAdmits;
        long lastReportTick;
        string lastState = "";
        int lastGen2;

        public long ReserveBytes => reserveBytes;
        public long CacheCapBytes => cacheCapBytes;
        public long LiveNativeBytes => Interlocked.Read(ref liveNativeBytes);
        public long OverReserveAdmits => Interlocked.Read(ref overReserveAdmits);

        public MemoryGovernor(Func<long, MemorySample?> sampler, long reserveBytes, long cacheCapBytes)
        {
            this.sampler = sampler;
            var s = sampler(0) ?? new MemorySample { TotalPhys = 4L << 30, AvailPhys = 2L << 30, AvailPageFile = long.MaxValue, LiveNativeAtSample = 0, Tick = Environment.TickCount64 };
            lastSample = s;
            long total = Math.Max(s.TotalPhys, 1L << 30);
            this.reserveBytes = reserveBytes > 0 ? Clamp(reserveBytes, total) : DefaultReserve(total);
            this.cacheCapBytes = cacheCapBytes > 0 ? cacheCapBytes : long.MaxValue;
            if (!Environment.Is64BitProcess) this.cacheCapBytes = Math.Min(this.cacheCapBytes, 512L << 20);
            lastReportTick = Environment.TickCount64;
            lastGen2 = GC.CollectionCount(2);
        }

        internal MemoryGovernor(Func<long, MemorySample?> sampler, long reserveBytes, long cacheCapBytes, bool autoTick)
            : this(sampler, reserveBytes, cacheCapBytes) => this.autoTick = autoTick;

        /// <summary>Test seam: refresh the memory sample without the shrink/report a full Tick does.</summary>
        internal void ForceSample() => RefreshSample();

        static long DefaultReserve(long total) => Clamp(Math.Max(1L << 30, total * 8 / 100), total);
        static long Clamp(long reserve, long total) => Math.Clamp(reserve, 256L << 20, total * 90 / 100);

        static MemoryGovernor? theDefault;
        public static MemoryGovernor Default => theDefault ??= new MemoryGovernor(live => Win32MemorySampler.TrySample(live), 0, 0);

        /// <summary>Apply CLI/env overrides to the process governor and log the startup line. Call once,
        /// before any cache is constructed.</summary>
        public static void Configure(long? reserveMb, long? cacheCapMb, string source)
        {
            var g = Default;
            var s = g.lastSample;
            long total = Math.Max(s.TotalPhys, 1L << 30);
            if (reserveMb is long rmb && rmb > 0) { g.reserveBytes = Clamp(rmb << 20, total); g.configSource = source; }
            if (cacheCapMb is long cmb && cmb > 0) g.cacheCapBytes = cmb << 20;
            if (!Environment.Is64BitProcess) g.cacheCapBytes = Math.Min(g.cacheCapBytes, 512L << 20);
            Log.Information($"MEM governor: total {Fmt(total)}, reserve {Fmt(g.reserveBytes)} ({g.configSource}), cache cap {(g.cacheCapBytes == long.MaxValue ? "none" : Fmt(g.cacheCapBytes))}, tick 1s");
        }

        public void Register(IGovernedCache cache)
        {
            lock (registryLock)
            {
                registry.Add(new WeakReference<IGovernedCache>(cache));
                if (autoTick)
                    timer ??= new Timer(_ => { try { Tick(); } catch (Exception ex) { Log.Debug($"MEM governor tick failed: {ex.Message}"); } },
                                        null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        public void Unregister(IGovernedCache cache)
        {
            lock (registryLock)
            {
                registry.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, cache));
            }
        }

        // ---- admission -------------------------------------------------------------------------------

        /// <summary>Reserve <paramref name="bytes"/> of cache memory, evicting globally-oldest spans first
        /// if the machine is near its reserve. Never throws and never blocks a reader indefinitely: if
        /// everything is at its floor it admits anyway (over-reserve) rather than stall a serving read.</summary>
        public void Admit(long bytes)
        {
            if (bytes <= 0) return;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                long live = Interlocked.Read(ref liveNativeBytes);
                var s = lastSample;
                if (!measured || Environment.TickCount64 - s.Tick > StaleMs) break;   // stale/unmeasured -> slow path
                long availEst = s.Avail - (live - s.LiveNativeAtSample);
                bool fits = availEst - bytes >= reserveBytes && live + bytes <= cacheCapBytes;
                if (!fits) break;
                if (Interlocked.CompareExchange(ref liveNativeBytes, live + bytes, live) == live) return;
                // lost the race; retry the fast path a few times before falling back
            }
            AdmitSlow(bytes);
        }

        void AdmitSlow(long bytes)
        {
            lock (registryLock)
            {
                RefreshSampleIfStale();
                while (!FitsUnderLock(bytes))
                {
                    if (!EvictGlobalOldest(Math.Min(DeficitFor(bytes), EvictBatchBytes), out long freed) || freed == 0)
                    {
                        Interlocked.Increment(ref overReserveAdmits);
                        break;   // nothing left to evict (all at floor) - admit anyway, never stall a reader
                    }
                }
                Interlocked.Add(ref liveNativeBytes, bytes);
            }
        }

        bool FitsUnderLock(long bytes)
        {
            long live = Interlocked.Read(ref liveNativeBytes);
            if (live + bytes > cacheCapBytes) return false;
            if (!measured) return true;
            var s = lastSample;
            long availEst = s.Avail - (live - s.LiveNativeAtSample);
            return availEst - bytes >= reserveBytes;
        }

        long DeficitFor(long bytes)
        {
            long live = Interlocked.Read(ref liveNativeBytes);
            var s = lastSample;
            long availDeficit = measured ? reserveBytes - (s.Avail - (live - s.LiveNativeAtSample) - bytes) : 0;
            long capDeficit = (live + bytes) - cacheCapBytes;
            return Math.Max(Math.Max(availDeficit, capDeficit), 1);
        }

        public void Release(long bytes)
        {
            if (bytes != 0) Interlocked.Add(ref liveNativeBytes, -bytes);
        }

        public void CountHit() => Interlocked.Increment(ref hits);
        public void CountMiss() => Interlocked.Increment(ref misses);
        public void CountRedecode() => Interlocked.Increment(ref redecodes);

        // ---- eviction (under registryLock) -----------------------------------------------------------

        bool EvictGlobalOldest(long want, out long freed)
        {
            freed = 0;
            IGovernedCache? victim = null;
            long oldest = long.MaxValue;
            for (int i = registry.Count - 1; i >= 0; i--)
            {
                if (!registry[i].TryGetTarget(out var c)) { registry.RemoveAt(i); continue; }
                if (c.SlotCount <= FloorSlots) continue;
                long t = c.OldestTouchTick;
                if (t <= oldest) { oldest = t; victim = c; }
            }
            if (victim == null) return false;
            if (victim.TryEvict(want, out freed) && freed > 0)
            {
                Interlocked.Increment(ref evictions);
                Interlocked.Add(ref evictedBytes, freed);
                return true;
            }
            return false;
        }

        // ---- the 1 s tick ----------------------------------------------------------------------------

        internal void Tick()
        {
            RefreshSample();

            if (measured)
            {
                var s = lastSample;
                long slack = Math.Min(128L << 20, reserveBytes / 8);
                if (s.Avail < reserveBytes - slack)
                {
                    long target = reserveBytes - s.Avail;   // free this much to get back to the reserve
                    var sw = Stopwatch.StartNew();
                    long totalFreed = 0;
                    lock (registryLock)
                    {
                        while (totalFreed < target && EvictGlobalOldest(Math.Min(target - totalFreed, 4 * EvictBatchBytes), out long f) && f > 0)
                            totalFreed += f;
                    }
                    if (totalFreed > 0)
                    {
                        var line = $"MEM shrink: avail {Fmt(s.Avail)} < reserve {Fmt(reserveBytes)}; evicted {Fmt(totalFreed)} in {sw.ElapsedMilliseconds} ms";
                        if (target - totalFreed > 1L << 30) Log.Warning(line); else Log.Information(line);
                    }
                }
            }

            if (cacheCapBytes != long.MaxValue)
            {
                lock (registryLock)
                {
                    long over = Interlocked.Read(ref liveNativeBytes) - cacheCapBytes;
                    while (over > 0 && EvictGlobalOldest(Math.Min(over, 4 * EvictBatchBytes), out long f) && f > 0) over -= f;
                }
            }

            Report();
        }

        void RefreshSample()
        {
            long live = Interlocked.Read(ref liveNativeBytes);
            var s = sampler(live);
            if (s != null) lastSample = s;
            else if (measured)
            {
                measured = false;
                cacheCapBytes = Math.Min(cacheCapBytes, Math.Max(1L << 30, lastSample.TotalPhys / 4));
                Log.Warning($"MEM sampler unavailable; falling back to a fixed cache cap of {Fmt(cacheCapBytes)}");
            }
        }

        void RefreshSampleIfStale()
        {
            if (Environment.TickCount64 - lastSample.Tick > StaleMs) RefreshSample();
        }

        // ---- reporting (state change, or 30 s while active, or 10 min heartbeat) ---------------------

        void Report()
        {
            var s = lastSample;
            long live = Interlocked.Read(ref liveNativeBytes);
            long over = Interlocked.Read(ref overReserveAdmits);

            long cached = 0; int slots = 0; int streams = 0; bool anyAtFloor = false;
            lock (registryLock)
            {
                for (int i = registry.Count - 1; i >= 0; i--)
                {
                    if (!registry[i].TryGetTarget(out var c)) { registry.RemoveAt(i); continue; }
                    streams++; cached += c.CachedBytes; slots += c.SlotCount;
                    if (c.SlotCount <= FloorSlots) anyAtFloor = true;
                }
            }

            long slack = Math.Min(128L << 20, reserveBytes / 8);
            string state = !measured ? "Capped"
                         : anyAtFloor && s.Avail < reserveBytes ? "Floor"
                         : s.Avail < reserveBytes - slack ? "Shrinking"
                         : s.Avail <= reserveBytes + slack ? "AtReserve"
                         : "Growing";

            long now = Environment.TickCount64;
            long m = Interlocked.Read(ref misses), e = Interlocked.Read(ref evictions);
            bool active = m > 0 || e > 0;
            long elapsed = now - lastReportTick;
            bool due = state != lastState || (active && elapsed >= 30_000) || elapsed >= 600_000;
            if (!due) return;

            long h = Interlocked.Exchange(ref hits, 0);
            m = Interlocked.Exchange(ref misses, 0);
            long rd = Interlocked.Exchange(ref redecodes, 0);
            long ev = Interlocked.Exchange(ref evictions, 0);
            long evb = Interlocked.Exchange(ref evictedBytes, 0);
            int gen2now = GC.CollectionCount(2);
            int gen2 = gen2now - lastGen2; lastGen2 = gen2now;
            long inFlight = Math.Max(0, live - cached);
            using var proc = Process.GetCurrentProcess();
            long ws = proc.WorkingSet64, priv = proc.PrivateMemorySize64;

            Log.Information(
                $"MEM {state}: avail {Fmt(s.Avail)} (reserve {Fmt(reserveBytes)}) | cache {Fmt(cached)} in {streams} stream(s), {slots} slots | " +
                $"in-flight {Fmt(inFlight)} | pool out {Fmt(Buffers.RentedOutBytes)} | {elapsed / 1000}s: hits {h:N0} misses {m:N0} evicted {ev:N0} ({Fmt(evb)}) " +
                $"redecodes {rd:N0} over-reserve {over:N0} gen2 {gen2} | ws {Fmt(ws)} private {Fmt(priv)}");

            lastReportTick = now;
            lastState = state;
        }

        static string Fmt(long b) => b == long.MaxValue ? "none" : Extensions.BytesToString((ulong)Math.Max(0, b));
    }
}
