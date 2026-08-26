using DokanNet;
using libCommon;
using libCommon.Streams;
using libDokan.Processes;
using libDokan.VFS;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using rextractor;
using Serilog;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using static DokanNet.FormatProviders;
using static libDokan.VFS.Folders.Folder;
using FileAccess = DokanNet.FileAccess;

namespace libDokan
{
    public class DokanVFS(string volumeLabel, RootFolder root) : IDokanOperations
    {
        readonly RootFolder Root = root;
        private readonly string VolumeLabel = volumeLabel;
        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                      FileAccess.Execute |
                                      FileAccess.GenericExecute | FileAccess.GenericWrite |
                                      FileAccess.GenericRead;

        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        protected static string GetPath(string fileName)
        {
            return fileName;
        }

        protected static NtStatus Trace(string method, string? fileName, IDokanFileInfo? info, NtStatus result, params object?[] parameters)
        {
            //skip all the message construction when Debug logging is off (no-op while the global level is Debug, but free future-proofing)
            if (!Log.IsEnabled(LogEventLevel.Debug)) return result;

            var extraParameters = parameters != null && parameters.Length > 0
                ? ", " + string.Join(", ", parameters.Select(x => string.Format(DefaultFormatProvider, "{0}", x)))
                : string.Empty;

            Log.Debug(DokanFormat($"{method}('{fileName}', {info}{extraParameters}) -> {result}"));

            return result;
        }

        private static NtStatus Trace(string method, string fileName, IDokanFileInfo info,
            FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes,
            NtStatus result)
        {
            if (!Log.IsEnabled(LogEventLevel.Debug)) return result;

            Log.Debug(
                DokanFormat(
                    $"{method}('{fileName}', {info}, [{access}], [{share}], [{mode}], [{options}], [{attributes}]) -> {result}"));

            return result;
        }

        const long STATUS_FILE_IS_A_DIRECTORY = 0xC00000BAL;
        const FileOptions FileNonDirectoryFile = (FileOptions)0x40;

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
            FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            var result = DokanResult.Success;
            var filePath = GetPath(fileName);

            //L11 defense-in-depth: start every open with a provably-clean context. Forensics
            //(2026-08-08, TEST_ANALYSIS.md "L11 CAUSE") showed contexts never actually arrive at
            //CreateFile - dokany zeroes both its pooled DOKAN_IO_EVENT and DOKAN_OPEN_INFO structs
            //on reuse (dokan_pool.c) - so this is pure insurance. Crucially it must be TryZero and
            //NOT `info.Context = null`: DokanNet's setter calls GCHandle.Free() on whatever number
            //is present, and if that number were stale its slot could since have been recycled to
            //ANOTHER file's live handle - freeing it would detonate that open. Zero-without-free
            //leaks at worst one small object; freeing a foreign live handle corrupts the process.
            if (ContextForensics.Enabled) ContextForensics.OnCreateEntry(info, fileName);
            DokanRawContext.TryZero(info);

            if (info.IsDirectory)
            {
                try
                {
                    switch (mode)
                    {
                        case FileMode.Open:
                            if (Root.GetEntryFromPath(filePath, info.ProcessId) is not Folder)
                            {
                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.PathNotFound);
                            }

                            break;

                        case FileMode.CreateNew:
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.NotImplemented);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.AccessDenied);
                }
            }
            else
            {
                var pathExists = true;
                var pathIsDirectory = false;

                var readWriteAttributes = (access & DataAccess) == 0;
                var readAccess = (access & DataWriteAccess) == 0;

                var fileSystemEntry = Root.GetEntryFromPath(filePath, info.ProcessId);
                pathExists = fileSystemEntry != null;
                pathIsDirectory = fileSystemEntry is Folder;

                //Without this block, some programs can't enumerate the drive. Eg. log4jscanner.exe
                //See: https://github.com/dokan-dev/dokan-dotnet/issues/274
                if (pathIsDirectory)
                {
                    // Explorer opens directories with GenericRead and expects success.
                    // Previously we treated GenericRead as "NonDirectoryFile", which broke folder copies
                    // (especially for special folders like Users) and caused UAC prompts / failures.
                    if ((options & FileNonDirectoryFile) != 0)
                    {
                        return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                            (NtStatus)STATUS_FILE_IS_A_DIRECTORY);
                    }

                    // CRITICAL: Do NOT return failure here just because access is GenericRead.
                    // Explorer NEEDS GenericRead to copy folders.

                    info.IsDirectory = true;
                }

                switch (mode)
                {
                    case FileMode.Open:

                        if (pathExists)
                        {
                            // check if driver only wants to read attributes, security info, or open directory
                            if (readWriteAttributes || pathIsDirectory)
                            {
                                if (pathIsDirectory && (access & FileAccess.Delete) == FileAccess.Delete
                                    && (access & FileAccess.Synchronize) != FileAccess.Synchronize)
                                    //It is a DeleteFile request on a directory
                                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                        attributes, DokanResult.AccessDenied);

                                info.IsDirectory = pathIsDirectory;
                                // info.Context = new object();
                                // must set it to something if you return DokanError.Success

                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.Success);
                            }
                        }
                        else
                        {
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        }
                        break;

                    case FileMode.CreateNew:
                        if (pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileExists);
                        break;

                    case FileMode.Truncate:
                        if (!pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        break;
                }

                try
                {
                    if (fileSystemEntry is FileEntry file)
                    {
                        info.Context = new FileEntryStream()
                        {
                            FileEntry = file,
                            Stream = file.GetStream()
                        };
                        if (ContextForensics.Enabled) ContextForensics.OnAlloc(info, fileName);
                    }
                }
                catch (UnauthorizedAccessException) // don't have access rights
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.AccessDenied);
                }
                catch (DirectoryNotFoundException)
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.PathNotFound);
                }
                catch (Exception ex)
                {
                    //never let an exception escape a Dokan callback - it surfaces to the app as 0x800705AA
                    Log.Error(ex, $"CreateFile failed opening a stream for '{fileName}'.");
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.Error);
                }
            }
            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                result);
        }

        //THE L11 CURE (2026-08-08): release the per-handle context at CloseFile ONLY - never at
        //Cleanup. info.Context is a raw GCHandle number, and every in-flight operation on this
        //open carries its own per-event copy of it (dokany copies UserContext into each event at
        //setup - dokan.c, SetupIOEventForProcessing). Freeing at Cleanup - while a killed client's
        //slow reads were still in flight - left those reads holding a dangling number that the
        //runtime recycled to the next CreateFile within milliseconds; their next info.Context
        //access then resolved to ANOTHER file's live stream. That was the entire cross-file bleed
        //mechanism (forensics: TEST_ANALYSIS.md "L11 CAUSE FOUND"). dokany defers the CloseFile
        //callback until OpenCount==0, i.e. until every in-flight event on this open has drained
        //(dokan.c, ReleaseDokanOpenInfo) - so CloseFile is the one point where a free can never
        //strand a live copy in a concurrent operation.
        static void ReleaseContext(IDokanFileInfo info, string fileName)
        {
            if (info.Context is FileEntryStream fileEntryStream)
            {
                if (ContextForensics.Enabled) ContextForensics.OnRelease(info, fileName, fileEntryStream.FileEntry.Name);

                //Belt-and-braces: if the context names another file (should be impossible now that
                //nothing frees early), neither dispose nor free it - it may be another open's LIVE
                //stream, and DokanNet's setter would GCHandle.Free it. Zero-without-free instead;
                //ReadFile's by-name path correctly serves any straggler operations.
                if (!string.Equals(fileName.AsSpan(fileName.LastIndexOf('\\') + 1).ToString(),
                                   fileEntryStream.FileEntry.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning($"CloseFile context mismatch: '{fileName}' holds a stream for "
                              + $"'{fileEntryStream.FileEntry.Name}'. Dropping it without dispose/free.");
                    DokanRawContext.TryZero(info);
                    return;
                }

                //only dispose streams the handle owns; shared streams (eg. mounted partition images) live for the lifetime of the VFS
                if (fileEntryStream.FileEntry.CreatesNewStreamPerCall)
                {
                    //ReadLock kept as pure defense: with the release moved to CloseFile, dokany
                    //guarantees no ReadFile can still be running on this open.
                    lock (fileEntryStream.ReadLock)
                    {
                        try
                        {
                            fileEntryStream.Stream.Dispose();
                        }
                        catch { }
                    }
                }

                info.Context = null;   //safe here, and ONLY here: no in-flight op holds a copy
            }
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Log.Debug(DokanFormat($"{nameof(Cleanup)}('{fileName}', {info} - entering"));
#endif

            //Deliberately does NOT release the context. Freeing it here was the L11 root cause:
            //Cleanup arrives while in-flight reads still carry copies of the GCHandle number (a
            //killed client's reads outlive its handles), and dokany only guarantees the open is
            //quiescent at CloseFile. See ReleaseContext.

            Trace(nameof(Cleanup), fileName, info, DokanResult.Success);
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Log.Debug(DokanFormat($"{nameof(CloseFile)}('{fileName}', {info} - entering"));
#endif

            ReleaseContext(info, fileName);

            Trace(nameof(CloseFile), fileName, info, DokanResult.Success);
        }

        //Dokan abandons an operation that outlives options.TimeOut (surfacing to the app as 0x800705AA,
        //and at the threshold it can unmount the volume). A few reads are genuinely slow - a heavily
        //fragmented file (e.g. a log) scatters its clusters across the partition, so reading it seeks all
        //over the compressed stream and can take tens of seconds, occasionally minutes. For those, keep the
        //operation alive by extending its deadline while it runs.
        //
        //How long is "too long"? A FIXED elapsed cap is wrong: a 3.4 MB bzip2-scattered log can legitimately
        //need several minutes of continuous decode, so any cap large enough to let it finish is also large
        //enough to sit uselessly on a real hang. Instead we extend for as long as the mount is *making
        //decode progress* (CachingStream.DecodeProgress, a global counter bumped once per span decoded) and
        //stop only after TimeoutWatchdogStuckMs with NO decode anywhere - i.e. the pipeline is genuinely
        //stuck, not merely slow. A pathological-but-progressing file thus runs to completion (gh #87: it used
        //to die at the 10-min cap, and the external 7z reading the .img - which has no read-retry - hung),
        //while a true deadlock still fails within the stuck window.
        //
        //A drive-image tree-walk issues millions of tiny fast reads, so we must NOT allocate a Timer per
        //read (alloc + schedule + dispose, each taking the global timer-queue lock - measured as the single
        //biggest cost of these reads). Instead ONE free-running timer watches a registry of in-flight reads:
        //a fast read enters and leaves the registry between ticks and is never touched, paying only a
        //dictionary insert/remove. The timer only ever extends reads still running at a tick = the slow ones.
        const int TimeoutWatchdogExtensionMs = 20_000;                                 //push the deadline out by this each tick
        static readonly TimeSpan TimeoutWatchdogInterval = TimeSpan.FromSeconds(5);    //tick well inside the 20s timeout
        //Abandon a slow read only after this long with the whole mount decoding nothing. Env override
        //(CLONEZILLA_READ_STUCK_SECONDS) for tuning; the default is generous because the cost of over-waiting
        //is just a stuck op held a little longer, while under-waiting reintroduces gh #87.
        static readonly long TimeoutWatchdogStuckMs =
            (long.TryParse(Environment.GetEnvironmentVariable("CLONEZILLA_READ_STUCK_SECONDS"), out var stuckSec) && stuckSec > 0
                ? stuckSec : 120) * 1000L;

        //An in-flight read the watchdog may extend. `Completed` (guarded by lock(this)) closes a
        //use-after-free: an IDokanFileInfo's native handle is valid ONLY while its ReadFile callback is on
        //the stack. The read's Dispose runs inside ReadFile (before it returns) and sets Completed under the
        //lock; the timer takes the same lock and only calls TryResetTimeout when Completed is false - i.e.
        //while the callback is provably still running. Without this, once the serving path actually runs in
        //parallel (many slow reads registered at once), a tick racing a completing read calls
        //DokanResetTimeout on a freed handle and crashes the process with 0xC0000005 (a native access
        //violation that the try/catch cannot catch).
        sealed class InFlightRead(IDokanFileInfo info, long startedTick, string fileName, long offset, int length)
        {
            public readonly IDokanFileInfo Info = info;
            public readonly long StartedTick = startedTick;
            public bool Completed;   //guarded by lock(this)

            //identity for the slow-read report (SlowReadReporting): which read is stalling, and where
            public readonly string FileName = fileName;
            public readonly long Offset = offset;
            public readonly int Length = length;

            //adaptive-cap bookkeeping (guarded by lock(this)): the DecodeProgress value we last saw, and the
            //tick at which it last advanced. While decode is happening the read is "progressing" and we keep
            //extending; once LastProgressTick is older than TimeoutWatchdogStuckMs the mount is stuck.
            public long ProgressSnapshot = startedTick;   //placeholder; set to the live counter in StartTimeoutWatchdog
            public long LastProgressTick = startedTick;
        }

        static readonly ConcurrentDictionary<long, InFlightRead> InFlightReads = new();
        static long inFlightReadIdSeq;

        static readonly Timer TimeoutWatchdog = new(_ =>
        {
            if (InFlightReads.IsEmpty)
            {
                SlowReadReporting.OnTick(Environment.TickCount64, 0, 0, null, 0);
                return;
            }
            var now = Environment.TickCount64;
            //Read the global decode counter ONCE per tick (not per read): "is the mount decoding anything?"
            //is a whole-pipeline question, and a single snapshot keeps every in-flight read judged against the
            //same instant.
            var decodeProgress = Interlocked.Read(ref CachingStream.DecodeProgress);
            var inFlight = 0;
            var slow = 0;
            InFlightRead? slowest = null;
            long slowestElapsed = 0;
            foreach (var read in InFlightReads.Values)
            {
                var elapsed = now - read.StartedTick;
                inFlight++;
                if (elapsed >= SlowReadThresholdMs)
                {
                    slow++;
                    if (elapsed > slowestElapsed) { slowestElapsed = elapsed; slowest = read; }
                }
                lock (read)
                {
                    if (read.Completed) continue;   //callback has returned; its native handle may be freed
                    //decode advanced since we last looked -> the mount is making progress; restart this read's
                    //stuck timer. (Any read benefits from any decode: they share the one serving pipeline.)
                    if (decodeProgress != read.ProgressSnapshot)
                    {
                        read.ProgressSnapshot = decodeProgress;
                        read.LastProgressTick = now;
                    }
                    if (now - read.LastProgressTick > TimeoutWatchdogStuckMs) continue;   //stuck - stop extending so it finally fails
                    try { read.Info.TryResetTimeout(TimeoutWatchdogExtensionMs); } catch { }
                }
            }
            SlowReadReporting.OnTick(now, inFlight, slow, slowest, slowestElapsed);
        }, null, TimeoutWatchdogInterval, TimeoutWatchdogInterval);

        //--- slow-read reporting -------------------------------------------------------------------
        //The 2026-08-18 bzip2 golden run lost 191 reads to Dokan timeouts (0x800705AA) in five bursts,
        //and 188 of them left NO trace in our log: they were slow, not broken. A read that dies of a
        //timeout throws nothing, so exceptions can never explain a stall. This reports stalls WHILE
        //THEY HAPPEN, off the existing watchdog tick (zero fast-path cost - the tick already walks the
        //in-flight registry): a line whenever the number of slow reads changes (and every 30s while a
        //stall persists) naming the slowest read, plus a line as each slow read completes giving its
        //total duration. How to read the pattern:
        //  many reads slow at once, all long          => the serving pipeline stalled, or the box was starved
        //  one read slow while others flow            => that file is pathological (fragmented / far seeks)
        //  slow reads that log a completion           => the watchdog kept them alive; the client saw a
        //                                                slow read, not an error
        //  a stall with no completions, then timeouts => no decode progress for the stuck window; the mount
        //                                                deadlocked and the watchdog stopped extending them
        const long SlowReadThresholdMs = 5_000;

        static class SlowReadReporting
        {
            static int lastReportedSlow;
            static long lastReportTick;
            const long RepeatIntervalMs = 30_000;   //while a stall persists, re-report at most this often

            public static void OnTick(long now, int inFlight, int slow, InFlightRead? slowest, long slowestElapsed)
            {
                //report on any change in the slow count, and periodically while it stays non-zero
                var changed = slow != lastReportedSlow;
                var due = slow > 0 && now - lastReportTick >= RepeatIntervalMs;
                if (!changed && !due) return;
                lastReportedSlow = slow;
                lastReportTick = now;

                if (slow == 0)
                {
                    Log.Information("SLOWREAD cleared: {InFlight} read(s) in flight, none over {ThresholdSec}s", inFlight, SlowReadThresholdMs / 1000);
                    return;
                }
                Log.Warning("SLOWREAD {Slow} of {InFlight} in-flight read(s) over {ThresholdSec}s; slowest {SlowestSec:N0}s: '{File}' @ {Offset:N0} len {Length:N0}",
                    slow, inFlight, SlowReadThresholdMs / 1000, slowestElapsed / 1000.0,
                    slowest?.FileName, slowest?.Offset ?? 0, slowest?.Length ?? 0);
            }

            //called from WatchdogRegistration.Dispose for reads that ended up slow: how long the read
            //took to complete. This fires when ReadFile RETURNS, so the read did finish. As long as the mount
            //kept decoding, the watchdog kept this read's Dokan deadline alive the whole time, so a completion
            //here - however long - means the client was served rather than timed out.
            public static void OnSlowReadCompleted(InFlightRead read, long elapsedMs)
            {
                Log.Warning("SLOWREAD done after {Sec:N1}s: '{File}' @ {Offset:N0} len {Length:N0}",
                    elapsedMs / 1000.0, read.FileName, read.Offset, read.Length);
            }
        }

        //Registers the current read with the shared watchdog for its duration; Dispose() unregisters it.
        //The handle is a struct, so `using var` disposes it with no allocation.
        static WatchdogRegistration StartTimeoutWatchdog(IDokanFileInfo info, string fileName, long offset, int length)
        {
            var id = Interlocked.Increment(ref inFlightReadIdSeq);
            var read = new InFlightRead(info, Environment.TickCount64, fileName, offset, length);
            read.ProgressSnapshot = Interlocked.Read(ref CachingStream.DecodeProgress);   //baseline: extend only on decode AFTER this
            InFlightReads[id] = read;
            return new WatchdogRegistration(id);
        }

        readonly struct WatchdogRegistration(long id) : IDisposable
        {
            public void Dispose()
            {
                if (InFlightReads.TryRemove(id, out var read))
                {
                    //Mark completed under the same lock the timer uses, so a concurrent tick can't call
                    //TryResetTimeout on this read once ReadFile returns and the native handle is freed.
                    lock (read) { read.Completed = true; }

                    var elapsed = Environment.TickCount64 - read.StartedTick;
                    if (elapsed >= SlowReadThresholdMs) SlowReadReporting.OnSlowReadCompleted(read, elapsed);
                }
            }
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            //Console.WriteLine($"ReadFile {buffer.Length:N0} bytes: {fileName}");

            bytesRead = 0;

            //extend the Dokan deadline if this read runs long (some fragmented files are genuinely slow).
            //Just registers with the shared watchdog (a dictionary insert/remove); a fast read is gone
            //before the next tick, so the common path pays no Timer alloc.
            using var watchdog = StartTimeoutWatchdog(info, fileName, offset, buffer.Length);

            // A Dokan callback must never throw: an unhandled exception becomes a generic driver failure
            // that surfaces to the calling app as 0x800705AA (ERROR_NO_SYSTEM_RESOURCES). Return a status.
            try
            {
            //Read the native context exactly ONCE. Every info.Context access dereferences the raw
            //GCHandle stored in this operation's native DOKAN_FILE_INFO; if that value is stale
            //(see the L11 notes below) its target can differ between two reads.
            var contextObj = info.Context;

            if (contextObj == null) // memory mapped read
            {
                var fileSystemEntry = Root.GetEntryFromPath(fileName, info.ProcessId);
                if (fileSystemEntry is FileEntry file)
                {
                    //paging reads have no handle context; FileEntry keeps one reusable stream for them
                    //instead of opening (and disposing) one per page fault
                    bytesRead = file.ReadForMemoryMap(buffer, offset, buffer.Length);
                }
                else
                {
                    return Trace(nameof(ReadFile), fileName, info, DokanResult.FileNotFound);
                }
            }
            else // normal read
            {
                if (contextObj is not FileEntryStream stream)
                {
                    if (ContextForensics.Enabled) ContextForensics.OnNonStream(info, fileName, contextObj.GetType().FullName ?? "?");
                    return Trace(nameof(ReadFile), fileName, info, DokanResult.Unsuccessful);
                }


                //FIX (L11, part 2 of 2): refuse to serve a context that belongs to another file.
                //Part 1 stops a stale context leaking through CreateFile, but it cannot help if the
                //driver hands back a recycled context WITHOUT a fresh CreateFile - which is exactly
                //what the diagnostic caught ("same FileEntryStream instance first served A, now
                //asked for B"). So the read path itself must not trust the context: verify it names
                //the file being requested, and if it does not, serve the file by NAME instead.
                //That turns what was silent cross-file corruption into a correct (marginally
                //slower) read, which is the right trade in a filesystem.
                if (!string.Equals(fileName.AsSpan(fileName.LastIndexOf('\\') + 1).ToString(),
                                   stream.FileEntry.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (ContextForensics.Enabled) ContextForensics.OnForeignRead(info, fileName, stream.FileEntry.Name);
                    Log.Warning($"ReadFile context mismatch: '{fileName}' was handed a stream for "
                              + $"'{stream.FileEntry.Name}'. Serving by name instead.");

                    //Self-heal the open: zero this event's copy WITHOUT freeing (the number may be
                    //another open's live handle). When this op completes, dokany writes the 0 back
                    //to the shared UserContext, so subsequent ops on this open take the (correct)
                    //by-name path instead of re-dereferencing the poisoned number - and DokanNet's
                    //CloseFileProxy finally-block no longer double-frees it at close.
                    DokanRawContext.TryZero(info);

                    if (Root.GetEntryFromPath(GetPath(fileName), info.ProcessId) is FileEntry byName)
                    {
                        bytesRead = byName.ReadForMemoryMap(buffer, offset, buffer.Length);
                        return Trace(nameof(ReadFile), fileName, info, DokanResult.Success,
                            "out " + bytesRead.ToString(), offset.ToString(CultureInfo.InvariantCulture));
                    }
                    return Trace(nameof(ReadFile), fileName, info, DokanResult.FileNotFound);
                }

                int DoRead()
                {
                    var toRead = Math.Min(stream.FileEntry.Length - offset, buffer.Length);
                    if (!Environment.Is64BitOperatingSystem)
                    {
                        toRead = Math.Min(toRead, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
                    }
                    toRead = Math.Min(toRead, Array.MaxLength);
                    toRead = Math.Max(toRead, 0);   //reads beyond EOF would otherwise produce a negative count

                    stream.Stream.Position = offset;
                    return stream.Stream.ReadAtLeast(buffer, (int)toRead, false);
                }

                //A per-handle stream only needs to guard concurrent reads on the SAME handle, so lock the
                //per-handle wrapper - different handles to the same file then run in parallel. A shared
                //stream (one per file across every handle) must serialize on the FileEntry instead.
                var readLock = stream.FileEntry.CreatesNewStreamPerCall ? stream.ReadLock : stream.FileEntry.ReadLock;
                lock (readLock)
                {
                    bytesRead = DoRead();
                }
            }
            return Trace(nameof(ReadFile), fileName, info, DokanResult.Success, "out " + bytesRead.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"ReadFile failed for '{fileName}' at offset {offset}.");
                bytesRead = 0;
                return Trace(nameof(ReadFile), fileName, info, DokanResult.Error,
                    offset.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            bytesWritten = 0;

            return Trace(nameof(WriteFile), fileName, info, DokanResult.NotImplemented, "out " + bytesWritten.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            try
            {
                if (info.Context is FileEntryStream fileEntry)
                {
                    fileEntry.Stream.Flush();
                }
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.Success);
            }
            catch (IOException)
            {
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.DiskFull);
            }
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            //Console.WriteLine($"GetFileInformation: {fileName}");

            // may be called with info.Context == null, but usually it isn't

            fileInfo = default;

            var fileSystemEntry = Root.GetEntryFromPath(fileName, info.ProcessId);
            if (fileSystemEntry == null)
            {
                return Trace(nameof(GetFileInformation), fileName, info, DokanResult.FileNotFound);
            }

            fileInfo = fileSystemEntry.ToFileInformation();

            return Trace(nameof(GetFileInformation), fileName, info, DokanResult.Success);
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            // This function is not called because FindFilesWithPattern is implemented
            // Return DokanResult.NotImplemented in FindFilesWithPattern to make FindFiles called
            try
            {
                files = FindFilesHelper(fileName, "*", info.ProcessId);
                return Trace(nameof(FindFiles), fileName, info, DokanResult.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"FindFiles failed for '{fileName}'.");
                files = Array.Empty<FileInformation>();
                return Trace(nameof(FindFiles), fileName, info, DokanResult.Error);
            }
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            //this is a read-only virtual file system; attributes cannot be changed
            return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.NotImplemented, attributes.ToString());
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            return Trace(nameof(SetFileTime), fileName, info, DokanResult.NotImplemented, creationTime, lastAccessTime, lastWriteTime);
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            //this is a read-only virtual file system
            return Trace(nameof(DeleteFile), fileName, info, DokanResult.AccessDenied);
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            //this is a read-only virtual file system
            return Trace(nameof(DeleteDirectory), fileName, info, DokanResult.AccessDenied);
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            return Trace(nameof(MoveFile), oldName, info, DokanResult.NotImplemented, newName,
                replace.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            return NtStatus.NotImplemented;
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            return NtStatus.NotImplemented;
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        long? cachedTotalInUse;
        DateTime cachedTotalInUseTime;
        readonly object diskFreeSpaceLock = new();

        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
        {
            long totalInUse;

            //walking the whole tree is expensive and this gets called often; memoize briefly
            lock (diskFreeSpaceLock)
            {
                if (cachedTotalInUse == null || (DateTime.Now - cachedTotalInUseTime) > TimeSpan.FromSeconds(10))
                {
                    long sum = 0;
                    _ = new Folder[] { Root }
                        .Recurse(folder =>
                        {
                            var totalFileSizes = folder
                                                    .Children
                                                    .OfType<FileEntry>()
                                                    .Sum(f => f.Length);

                            sum += totalFileSizes;

                            var subfolders = folder
                                                .Children
                                                .OfType<Folder>()
                                                .ToList();

                            return subfolders;
                        })
                        .ToList();

                    cachedTotalInUse = sum;
                    cachedTotalInUseTime = DateTime.Now;
                }

                totalInUse = cachedTotalInUse.Value;
            }

            totalNumberOfBytes = totalInUse * 10;

            totalNumberOfFreeBytes = totalNumberOfBytes - totalInUse;
            freeBytesAvailable = totalNumberOfFreeBytes;

            return Trace(nameof(GetDiskFreeSpace), null, info, DokanResult.Success, "out " + freeBytesAvailable.ToString(),
                "out " + totalNumberOfBytes.ToString(), "out " + totalNumberOfFreeBytes.ToString());
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
            out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            volumeLabel = VolumeLabel;
            fileSystemName = "clonezilla-util";
            maximumComponentLength = 256;

            //note: no CaseSensitiveSearch, because path lookups are case-insensitive
            features = FileSystemFeatures.CasePreservedNames |
                        FileSystemFeatures.UnicodeOnDisk |
                        FileSystemFeatures.ReadOnlyVolume;

            return Trace(nameof(GetVolumeInformation), null, info, DokanResult.Success, "out " + volumeLabel,
                "out " + features.ToString(), "out " + fileSystemName);
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections,
                            IDokanFileInfo info)
        {
            security = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
            IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return Trace(nameof(Mounted), null, info, DokanResult.Success);
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return Trace(nameof(Unmounted), null, info, DokanResult.Success);
        }

        public NtStatus FindStreams(string fileName, IntPtr enumContext, out string streamName, out long streamSize,
            IDokanFileInfo info)
        {
            streamName = string.Empty;
            streamSize = 0;
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented, enumContext.ToString(),
                "out " + streamName, "out " + streamSize.ToString());
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = Array.Empty<FileInformation>();
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented);
        }

        public IList<FileInformation> FindFilesHelper(string fileName, string searchPattern, int requestingPID)
        {
            //Console.WriteLine($"FindFilesHelper: {fileName}                     {searchPattern}");

            var fileSystemEntry = Root.GetEntryFromPath(fileName, requestingPID);

            var result = new List<FileInformation>();

            if (fileSystemEntry != null && !fileSystemEntry.IsAccessibleToProcess(requestingPID))
            {
                return result;
            }

            var wildcardMatcher = new FindFilesPatternToRegex();

            if (fileSystemEntry is Folder folder)
            {
                //This was too slow for L:\partition1\Windows\WinSxS. External applications would get 'Insufficient resources' timeouts because it took longer than 20 seconds to run.
                /*
                result = folder
                            .Children
                            .Where(child => FindFilesPatternToRegex.FindFilesEmulator(searchPattern, child.Name))   //This is slow, because it has to compile the Regex object for every single file in the folder
                            .Where(child => child is not UnlistedFolder)
                            .Select(entry => entry.ToFileInformation())
                            .ToList();
                */

                IList<FileSystemEntry> matchingChildren;
                if (searchPattern.Equals("*"))
                {
                    matchingChildren = folder.Children.ToList();
                }
                else
                {
                    matchingChildren = FindFilesPatternToRegex
                                        .FindFilesEmulator(searchPattern, folder.Children, item => item.Name);   //This is much faster, because it only has to compile the Regex object once for the folder
                }

                result = matchingChildren
                            .Where(child => child is not UnlistedFolder)
                            .Select(entry => entry.ToFileInformation())
                            .ToList();
            }
            else if (fileSystemEntry is FileEntry file)
            {
                var fileInfo = file.ToFileInformation();

                result.Add(fileInfo);
            }

            return result;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
            IDokanFileInfo info)
        {
            try
            {
                files = FindFilesHelper(fileName, searchPattern, info.ProcessId);
                return Trace(nameof(FindFilesWithPattern), fileName, info, DokanResult.Success);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"FindFilesWithPattern failed for '{fileName}' (pattern '{searchPattern}').");
                files = Array.Empty<FileInformation>();
                return Trace(nameof(FindFilesWithPattern), fileName, info, DokanResult.Error);
            }
        }

        public static void Test()
        {
            var rootFolder = new RootFolder(@"X:\");
            var subfolder1 = new Folder("2021-12-28-13-img_PB-DEVOPS1_gz", rootFolder);
            var subfolder2 = new Folder("extracted", subfolder1);

            var files = Directory
                        .GetFiles(@"E:\_img\3 - restored using clonezilla-util")
                        .Select(filename =>
                        {
                            var fi = new FileInfo(filename);

                            var file = new StreamBackedFileEntry(
                                fi.Name,
                                subfolder2,
                                () =>
                                {
                                    var stream = File.OpenRead(filename);
                                    return stream;
                                },
                                fi.Length
                                )
                            {
                                Length = fi.Length,
                                Created = fi.CreationTime,
                                Accessed = fi.LastAccessTime,
                                Modified = fi.LastWriteTime
                            };

                            return file;
                        })
                        .ToList();


            var testFS = new DokanVFS("DokanVFS", rootFolder);
            //testFS.Mount(rootFolder.MountPoint);
        }
    }


    public class FileEntryStream
    {
        public required FileEntry FileEntry;
        public required Stream Stream;

        //per-handle lock: serializes concurrent reads on THIS handle's stream, without blocking other
        //handles to the same file (used when the entry hands out a fresh stream per open handle)
        public readonly object ReadLock = new();
    }
}