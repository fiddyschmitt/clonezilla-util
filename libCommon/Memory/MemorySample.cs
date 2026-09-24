using System;
using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace libCommon.Memory
{
    /// <summary>
    /// An immutable snapshot of system memory at one instant. Reference type on purpose: the governor
    /// publishes a new one with a single atomic reference write, so hot-path readers see a consistent set
    /// of fields with no lock and no torn read.
    /// </summary>
    public sealed class MemorySample
    {
        public required long TotalPhys { get; init; }
        public required long AvailPhys { get; init; }
        public required long AvailPageFile { get; init; }
        public required long LiveNativeAtSample { get; init; }
        public required long Tick { get; init; }

        /// <summary>Headroom = the smaller of free physical RAM and free commit. Filling physical RAM past
        /// the commit limit would force the pagefile to grow (or fail), so both are respected.</summary>
        public long Avail => Math.Min(AvailPhys, AvailPageFile);
    }

    /// <summary>
    /// Samples system-wide memory. <c>GlobalMemoryStatusEx</c> is one cheap syscall that is always current
    /// (unlike <c>GC.GetGCMemoryInfo().MemoryLoadBytes</c>, which only refreshes at a GC). If the syscall
    /// ever fails it falls back to GC info, which still yields a usable available figure.
    /// </summary>
    static class Win32MemorySampler
    {
        static bool warned;

        public static MemorySample? TrySample(long liveNativeBytes)
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (PInvoke.GlobalMemoryStatusEx(ref status))
                {
                    return new MemorySample
                    {
                        TotalPhys = (long)status.ullTotalPhys,
                        AvailPhys = (long)status.ullAvailPhys,
                        AvailPageFile = (long)status.ullAvailPageFile,
                        LiveNativeAtSample = liveNativeBytes,
                        Tick = Environment.TickCount64,
                    };
                }
            }
            catch (Exception ex)
            {
                if (!warned) { warned = true; Log.Warning($"GlobalMemoryStatusEx failed ({ex.Message}); the memory governor will use GC memory info."); }
            }

            // Fallback: GC info. TotalAvailableMemoryBytes is total/limit; MemoryLoadBytes is system memory
            // in use (possibly stale between GCs). avail = total - load. No commit figure, so treat pagefile
            // as unbounded (physical is the binding constraint anyway).
            try
            {
                var gc = GC.GetGCMemoryInfo();
                var total = gc.TotalAvailableMemoryBytes;
                var avail = Math.Max(0, total - gc.MemoryLoadBytes);
                return new MemorySample
                {
                    TotalPhys = total,
                    AvailPhys = avail,
                    AvailPageFile = long.MaxValue,
                    LiveNativeAtSample = liveNativeBytes,
                    Tick = Environment.TickCount64,
                };
            }
            catch { return null; }
        }
    }
}
