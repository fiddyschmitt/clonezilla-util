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
