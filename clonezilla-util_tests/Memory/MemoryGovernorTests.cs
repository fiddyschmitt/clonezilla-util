using System;
using libCommon.Memory;

namespace clonezilla_util_tests.Memory
{
    // Fast in-process tests of the governor's accounting, eviction, shrink, cap, and never-block behaviour.
    // Every governor is driven by a FakeSampler with autoTick:false, so decisions are deterministic. Values
    // are GB-scale so the reserve's 256 MB floor / 90%-of-total ceiling never distort the arithmetic
    // (FakeCache slots are plain numbers, not real allocations, so any size is cheap).
    [TestClass]
    public class MemoryGovernorTests
    {
        const long GB = 1L << 30;

        static MemoryGovernor Gov(FakeSampler fs, long reserve, long cap = long.MaxValue) =>
            new MemoryGovernor(fs.Sample, reserve, cap, autoTick: false);

        [TestMethod]
        public void Admit_And_Release_Balance()
        {
            var g = Gov(new FakeSampler(60 * GB), reserve: 1 * GB);
            Assert.AreEqual(0, g.LiveNativeBytes);
            g.Admit(2 * GB);
            Assert.AreEqual(2 * GB, g.LiveNativeBytes);
            g.Release(2 * GB);
            Assert.AreEqual(0, g.LiveNativeBytes);
        }

        [TestMethod]
        public void Admit_EvictsGlobalOldest_AcrossInstances()
        {
            var fs = new FakeSampler(60 * GB);   // generous while filling
            var g = Gov(fs, reserve: 2 * GB);
            var a = new FakeCache(g, "A");
            var b = new FakeCache(g, "B");
            for (int i = 0; i < 5; i++) a.Add(1 * GB, touchTick: 100 + i);   // A is older
            for (int i = 0; i < 5; i++) b.Add(1 * GB, touchTick: 200 + i);   // B is newer

            fs.Avail = 2 * GB; g.ForceSample();   // now tight: a new admit must evict exactly one span

            g.Admit(1 * GB);

            Assert.AreEqual(4, a.SlotCount, "the OLDER instance should have lost exactly one slot");
            Assert.AreEqual(5, b.SlotCount, "the newer instance should be untouched");
        }

        [TestMethod]
        public void Tick_ShrinksWhenAvailBelowReserve_AndHonoursFloor()
        {
            var fs = new FakeSampler(60 * GB);
            var g = Gov(fs, reserve: 2 * GB);
            var a = new FakeCache(g, "A");
            for (int i = 0; i < 10; i++) a.Add(1 * GB, touchTick: 100 + i);   // 10 GB

            fs.Avail = 512L * 1024 * 1024;   // 0.5 GB, far below reserve
            g.Tick();

            Assert.AreEqual(8, a.SlotCount, "should have shed just enough to reach the reserve (2 slots)");
            Assert.IsTrue(a.SlotCount >= MemoryGovernor.FloorSlots, "the floor must be honoured");
        }

        //Windows counts its cached file data as in use, yet gives it up first when a process needs memory: a file
        //cache full of earlier work's data must not hold the decode cache down (2026-10-09: 31 GB of stale cache
        //held it at its floor for nine hours). Cached file data is not commit, though: the commit headroom still binds.
        [TestMethod]
        public void FileCacheAboveTheReserve_IsHeadroom()
        {
            var fs = new FakeSampler(60 * GB);
            var g = Gov(fs, reserve: 2 * GB);
            var a = new FakeCache(g, "A");
            for (int i = 0; i < 5; i++) a.Add(1 * GB, touchTick: 100 + i);

            //free memory at the reserve, 10 GB of file cache: an admit takes from the cache, evicting nothing
            fs.Avail = 2 * GB; fs.FileCache = 10 * GB; g.ForceSample();
            g.Admit(1 * GB);
            Assert.AreEqual(5, a.SlotCount, "the file cache above the reserve should have made room");

            //a file cache no bigger than the reserve isn't counted: the same admit evicts, as before
            fs.FileCache = 2 * GB; g.ForceSample();
            g.Admit(1 * GB);
            Assert.AreEqual(4, a.SlotCount, "with no reclaimable cache the admit should evict one span");

            //a big file cache with no commit headroom left: commit binds, the admit evicts
            fs.FileCache = 10 * GB; fs.AvailPageFile = 2 * GB; g.ForceSample();
            g.Admit(1 * GB);
            Assert.AreEqual(3, a.SlotCount, "the commit headroom should still bind");
        }

        //Free memory may dip to half the reserve while Windows trims its cache; if it falls further (the cache is in
        //active use and Windows won't let it go), the caches shed back to that floor however big the cache is
        [TestMethod]
        public void BelowHalfTheReserve_Sheds_EvenWithAFileCache()
        {
            var fs = new FakeSampler(60 * GB);
            var g = Gov(fs, reserve: 4 * GB);
            var a = new FakeCache(g, "A");
            for (int i = 0; i < 10; i++) a.Add(1 * GB, touchTick: 100 + i);

            fs.Avail = 2 * GB; fs.FileCache = 20 * GB;
            g.Tick();
            Assert.AreEqual(10, a.SlotCount, "at half the reserve, with cache to reclaim, nothing should be shed");

            fs.Avail = 1 * GB;
            g.Tick();
            Assert.AreEqual(9, a.SlotCount, "1 GB under the floor of half the reserve should shed 1 GB");
        }

        //Two spans a stream is no floor for concurrent readers: on 2026-10-09 the golden bzip2 leg's 16 readers spent
        //eight hours evicting each other's spans from 16 MB of cache. The reserve sheds only down to the byte floor,
        //and at the floor new spans replace the oldest instead of growing the cache.
        [TestMethod]
        public void Reserve_ShedsOnlyToTheByteFloor()
        {
            const long MB = 1L << 20;
            var fs = new FakeSampler(60 * GB);
            var g = Gov(fs, reserve: 2 * GB);
            Assert.AreEqual(1 * GB, g.FloorBytes, "a 64th of the 64 GB fake machine");
            var a = new FakeCache(g, "A");
            for (int i = 0; i < 48; i++) a.Add(64 * MB, touchTick: 100 + i);   //3 GB

            fs.Avail = 0;
            g.Tick();
            Assert.AreEqual(16, a.SlotCount, "with no memory at all the tick should shed to the 1 GB floor, not to two spans");

            //spans 132-147 survived; the first new span goes over the floor, each later one replaces the oldest
            for (int i = 0; i < 8; i++) a.Add(64 * MB, touchTick: 1000 + i);
            Assert.AreEqual(17, a.SlotCount, "at the floor each new span should replace the oldest");
            Assert.AreEqual(139, a.OldestTouchTick, "spans 132-138 should have been the ones replaced");
        }

        [TestMethod]
        public void Cap_IsEnforced()
        {
            var fs = new FakeSampler(60 * GB);   // avail never binding; only the cap governs
            var g = Gov(fs, reserve: 1 * GB, cap: 3 * GB);   // 3 slots of 1 GB
            var a = new FakeCache(g, "A");
            for (int i = 0; i < 6; i++) a.Add(1 * GB, touchTick: 100 + i);

            Assert.AreEqual(3, a.SlotCount, "the cap should cap the slot count");
            Assert.IsTrue(g.LiveNativeBytes <= 3 * GB, "total cached must not exceed the cap");
        }

        [TestMethod]
        public void OverReserve_WhenEverythingAtFloor_AdmitsAnyway()
        {
            var fs = new FakeSampler(60 * GB);
            var g = Gov(fs, reserve: 2 * GB);
            var a = new FakeCache(g, "A");
            a.Add(1 * GB, 1); a.Add(1 * GB, 2);   // exactly the floor

            fs.Avail = 512L * 1024 * 1024; g.ForceSample();
            long before = g.OverReserveAdmits;

            g.Admit(1 * GB);   // nothing evictable (at floor) -> must admit anyway rather than stall a reader

            Assert.IsTrue(g.OverReserveAdmits > before, "an unmet admit at the floor should be counted");
            Assert.AreEqual(3 * GB, g.LiveNativeBytes, "the admit still succeeded");
        }
    }
}
