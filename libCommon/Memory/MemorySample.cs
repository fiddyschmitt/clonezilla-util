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

        /// <summary>The system file cache's working set: Windows' cached file data, which it counts as in use
        /// rather than available, yet gives up first when a process needs memory. 0 when it can't be read.</summary>
        public long FileCache { get; init; }
    }

    /// <summary>
    /// Samples system-wide memory. <c>GlobalMemoryStatusEx</c> is one cheap syscall that is always current
    /// (unlike <c>GC.GetGCMemoryInfo().MemoryLoadBytes</c>, which only refreshes at a GC). If the syscall
    /// ever fails it falls back to GC info, which still yields a usable available figure. The file cache is
    /// one more cheap, unprivileged syscall (the figure behind the "System Cache Resident Bytes" counter).
    /// </summary>
    static class Win32MemorySampler
    {
        static bool warned, fileCacheWarned;

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
                        FileCache = FileCacheWorkingSet(),
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

        //The system file cache's working set, or 0 (none counted as reclaimable: the governor behaves as it did
        //before it looked at the cache) if it can't be read
        static long FileCacheWorkingSet()
        {
            try
            {
                var info = new SYSTEM_FILECACHE_INFORMATION();
                var status = NtQuerySystemInformation(SystemFileCacheInformation, ref info, Marshal.SizeOf<SYSTEM_FILECACHE_INFORMATION>(), out _);
                if (status == 0) return (long)info.CurrentSize;
                if (!fileCacheWarned) { fileCacheWarned = true; Log.Debug($"MEM: could not read the file cache size (NTSTATUS 0x{status:X8}); not counting it as reclaimable."); }
            }
            catch (Exception ex)
            {
                if (!fileCacheWarned) { fileCacheWarned = true; Log.Debug($"MEM: could not read the file cache size ({ex.Message}); not counting it as reclaimable."); }
            }
            return 0;
        }

        const int SystemFileCacheInformation = 21;

        [DllImport("ntdll.dll")]
        static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_FILECACHE_INFORMATION info, int length, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_FILECACHE_INFORMATION
        {
            public nuint CurrentSize, PeakSize;
            public uint PageFaultCount;
            public nuint MinimumWorkingSet, MaximumWorkingSet, CurrentSizeIncludingTransitionInPages, PeakSizeIncludingTransitionInPages;
            public uint TransitionRePurposeCount, Flags;
        }
    }
}
