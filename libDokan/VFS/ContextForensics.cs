using DokanNet;
using Serilog;
using System.Collections.Concurrent;

namespace libDokan.VFS
{
    //Diagnostic instrumentation for the L11 follow-up question: WHY does Dokan hand an operation a
    //context belonging to another file?
    //
    //Mechanism under test (established by reading DokanNet 2.3.0.3 source):
    //  - DokanFileInfo is a struct overlaying the PER-EVENT native DOKAN_FILE_INFO; info.Context
    //    stores a raw GCHandle value in it (setter = GCHandle.Alloc/Free, getter = unvalidated
    //    ((GCHandle)(nint)context).Target).
    //  - Every dispatched operation carries its own COPY of that number, taken from the driver's
    //    per-open state when the event was built.
    //  - Freeing the GCHandle (our ReleaseContext, or DokanNet's own CloseFileProxy finally-block)
    //    while another operation is still in flight leaves that operation holding a dangling
    //    number; the runtime recycles the freed slot on the next GCHandle.Alloc - typically a
    //    concurrent CreateFile for a DIFFERENT file - and the stale operation's getter then
    //    resolves to the wrong file's FileEntryStream.
    //
    //This class proves/refutes that empirically: it records the RAW context value (read through
    //DokanNet's internal DokanFileInfo* WITHOUT dereferencing the GCHandle - just the number, safe
    //even when dangling) at every alloc/free/inherit/foreign event, keeps a short history per
    //value, and dumps that history whenever a foreign context is observed. A foreign-read whose
    //history reads "alloc A -> free A -> alloc B" while the read asked for A and got B is the
    //smoking gun for GCHandle slot recycling.
    //
    //Enabled only when CLONEZILLA_CTX_FORENSICS=1. Pure observation: no behavior changes.
    public static class ContextForensics
    {
        public static readonly bool Enabled =
            Environment.GetEnvironmentVariable("CLONEZILLA_CTX_FORENSICS") == "1";

        //raw-value extraction ------------------------------------------------------------------

        //delegates to DokanRawContext (shared with the L11 cure paths in DokanVFS)
        static bool hookReported;

        public static long Raw(IDokanFileInfo info)
        {
            var ok = DokanRawContext.TryRead(info, out var raw);
            if (!hookReported)
            {
                hookReported = true;
                if (ok) Log.Information("CTXF hooked raw context field OK on {Type}", info.GetType().FullName);
                else Log.Warning("CTXF hook FAILED - raw values unavailable");
            }
            return raw;
        }

        //per-raw-value history -----------------------------------------------------------------

        sealed class RawHistory
        {
            public readonly Queue<(long Seq, long Tick, string Evt)> Events = new();
        }

        const int MaxEventsPerRaw = 16;
        static readonly ConcurrentDictionary<long, RawHistory> history = new();
        static long seq;

        static void Add(long raw, string evt)
        {
            var h = history.GetOrAdd(raw, _ => new RawHistory());
            lock (h)
            {
                h.Events.Enqueue((Interlocked.Increment(ref seq), Environment.TickCount64, evt));
                while (h.Events.Count > MaxEventsPerRaw) h.Events.Dequeue();
            }
        }

        static string Dump(long raw)
        {
            if (!history.TryGetValue(raw, out var h)) return "<no history>";
            var now = Environment.TickCount64;
            lock (h)
            {
                return string.Join("; ", h.Events.Select(e => $"#{e.Seq} t-{now - e.Tick}ms {e.Evt}"));
            }
        }

        //returns the latest event string for classification, or null
        static string? LastEvent(long raw)
        {
            if (!history.TryGetValue(raw, out var h)) return null;
            lock (h) { return h.Events.Count > 0 ? h.Events.Last().Evt : null; }
        }

        //counters ------------------------------------------------------------------------------

        static long allocs, frees, inheritedLive, inheritedStale, inheritedUnknown,
                    foreignReads, foreignReleases, nonStreamContexts;

        //keep detailed lines bounded; counters stay exact
        static bool ShouldLogDetail(long n) => n <= 200 || n % 100 == 0;

        //hooks ---------------------------------------------------------------------------------

        //CreateFile entry: the driver handed a BRAND-NEW create an existing context value.
        //Classify it against our history:
        //  LIVE  = we allocated this number for another open handle and never freed it
        //          (part 1's nulling is about to free a handle that other file still owns!)
        //  STALE = we freed this number earlier; the driver-side copy outlived the free
        //          (nulling it will double-free whatever occupies the slot now)
        public static void OnCreateEntry(IDokanFileInfo info, string fileName)
        {
            var raw = Raw(info);
            if (raw == 0) return;

            var last = LastEvent(raw);
            string cls; long n;
            if (last != null && last.StartsWith("alloc", StringComparison.Ordinal))
            { cls = "LIVE"; n = Interlocked.Increment(ref inheritedLive); }
            else if (last != null && last.StartsWith("free", StringComparison.Ordinal))
            { cls = "STALE"; n = Interlocked.Increment(ref inheritedStale); }
            else
            { cls = "UNKNOWN"; n = Interlocked.Increment(ref inheritedUnknown); }

            if (ShouldLogDetail(n))
                Log.Warning("CTXF create-inherited({Cls}) raw=0x{Raw:X} incoming='{File}' | {History}",
                    cls, raw, fileName, Dump(raw));
            Add(raw, $"inherit@create[{cls}] '{fileName}'");
        }

        //after CreateFile assigns info.Context = new FileEntryStream(...)
        public static void OnAlloc(IDokanFileInfo info, string fileName)
        {
            var raw = Raw(info);
            if (raw == 0) return;
            Interlocked.Increment(ref allocs);
            Add(raw, $"alloc '{fileName}'");
        }

        //ReleaseContext, before dispose/null. A name mismatch here means Cleanup/CloseFile for one
        //file is about to dispose and free ANOTHER file's live stream - the same foreign-context
        //hazard ReadFile's guard catches, on the release path.
        public static void OnRelease(IDokanFileInfo info, string fileName, string streamEntryName)
        {
            var raw = Raw(info);
            if (raw == 0) return;
            Interlocked.Increment(ref frees);

            var lastSegment = fileName[(fileName.LastIndexOf('\\') + 1)..];
            if (!string.Equals(lastSegment, streamEntryName, StringComparison.OrdinalIgnoreCase))
            {
                var n = Interlocked.Increment(ref foreignReleases);
                if (ShouldLogDetail(n))
                    Log.Warning("CTXF foreign-RELEASE raw=0x{Raw:X} cleanup-of='{File}' holds-stream-of='{Got}' | {History}",
                        raw, fileName, streamEntryName, Dump(raw));
            }
            Add(raw, $"free '{streamEntryName}' (via '{fileName}')");
        }

        //ReadFile name-guard hit: asked for one file, context resolved to another's stream
        public static void OnForeignRead(IDokanFileInfo info, string fileName, string streamEntryName)
        {
            var raw = Raw(info);
            var n = Interlocked.Increment(ref foreignReads);
            if (ShouldLogDetail(n))
                Log.Warning("CTXF foreign-READ raw=0x{Raw:X} asked='{File}' got='{Got}' | {History}",
                    raw, fileName, streamEntryName, Dump(raw));
            if (raw != 0) Add(raw, $"foreign-read asked'{fileName}' got'{streamEntryName}'");
        }

        //ReadFile: context resolved to something that is not a FileEntryStream at all - the
        //recycled slot belongs to a non-Dokan GCHandle user, or the slot content is garbage
        public static void OnNonStream(IDokanFileInfo info, string fileName, string typeName)
        {
            var raw = Raw(info);
            var n = Interlocked.Increment(ref nonStreamContexts);
            if (ShouldLogDetail(n))
                Log.Warning("CTXF non-stream-context raw=0x{Raw:X} file='{File}' targetType='{Type}' | {History}",
                    raw, fileName, typeName, Dump(raw));
            if (raw != 0) Add(raw, $"non-stream[{typeName}] '{fileName}'");
        }

        //periodic summary ----------------------------------------------------------------------

        //the repro harness kills the mount (no clean unmount), so emit summaries on a timer
        static long lastSummarySnapshot = -1;
        static readonly Timer? summaryTimer = Enabled
            ? new(_ => LogSummary(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60))
            : null;

        public static void LogSummary()
        {
            var snapshot = Interlocked.Read(ref allocs) + Interlocked.Read(ref frees)
                         + Interlocked.Read(ref inheritedLive) + Interlocked.Read(ref inheritedStale)
                         + Interlocked.Read(ref inheritedUnknown) + Interlocked.Read(ref foreignReads)
                         + Interlocked.Read(ref foreignReleases) + Interlocked.Read(ref nonStreamContexts);
            if (snapshot == lastSummarySnapshot) return;
            lastSummarySnapshot = snapshot;

            Log.Warning("CTXF summary: allocs={Allocs} frees={Frees} " +
                        "create-inherited live={Live}/stale={Stale}/unknown={Unknown} " +
                        "foreign-reads={FReads} foreign-releases={FRels} non-stream={NonStream} distinct-raws={Raws}",
                Interlocked.Read(ref allocs), Interlocked.Read(ref frees),
                Interlocked.Read(ref inheritedLive), Interlocked.Read(ref inheritedStale),
                Interlocked.Read(ref inheritedUnknown), Interlocked.Read(ref foreignReads),
                Interlocked.Read(ref foreignReleases), Interlocked.Read(ref nonStreamContexts),
                history.Count);
        }
    }
}
