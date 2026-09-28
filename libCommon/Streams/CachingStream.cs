using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using libCommon.Memory;
using Serilog;

namespace libCommon.Streams
{
    // ------------------------------------------------------------------------------------------------
    // Decoded-span cache. Slots live in NATIVE memory (NativeBlock) and their total size is managed
    // process-wide by MemoryGovernor: the cache grows until the machine's available RAM reaches a reserve,
    // and the governor sheds globally-oldest spans when anything else needs memory. This replaces the old
    // "¼ of total RAM split per partition" budget, which ignored what was free and only ever shrank inside
    // a cache miss (so an idle partition never gave memory back).
    //
    // Lock-ordering rules (also in MemoryGovernor):
    //  1. MemoryGovernor.registryLock > any instance lock (mapLock / listLock). The governor takes an
    //     instance lock only inside TryEvict.
    //  2. An instance never calls governor.Admit/Register/Unregister while holding its own lock. Only the
    //     Interlocked governor counters (Release/CountHit/...) are used under an instance lock.
    //  3. Legacy path: decodeLock > listLock; listLock nests nothing.
    //  4. A native pointer is dereferenced (CopyTo) and freed only under the owning instance's lock (or by
    //     NativeBlock's finalizer once the whole stream is unreachable). A use-after-free is an AV, so the
    //     copy-under-lock discipline is load-bearing, not just an anti-bleed nicety.
    //  5. slot.Ready.Wait() is never called under a lock.
    // ------------------------------------------------------------------------------------------------
    public class CachingStream : Stream, IPositionalReader, IGovernedCache
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => concurrent ? BaseStream.Length : LegacyLength();

        //A restart stream opened without a known length discovers it by decoding the whole stream through the
        //same stateful cursor the legacy decode path uses. Two first readers doing that at once (or one doing it
        //while another decodes) would interleave on that cursor and could persist a wrong length, so discovery is
        //serialised with decodes under decodeLock. Lengths are fixed, so it is read once.
        long legacyLength = -1;
        long LegacyLength()
        {
            long known = Volatile.Read(ref legacyLength);
            if (known >= 0) return known;
            lock (decodeLock)
            {
                if (legacyLength < 0) Volatile.Write(ref legacyLength, BaseStream.Length);
                return legacyLength;
            }
        }

        long position = 0;
        public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }

        public Stream BaseStream { get; }
        public IReadSuggestor? ReadSuggestor { get; }   //insight into the most optimal way to read the underlying stream

        readonly MemoryGovernor governor;
        readonly string name;
        volatile bool disposed;

        //total logical bytes cached in this instance's live slots (mutated only under the instance lock).
        long cachedBytes;
        //LastTouchTick of the LRU-tail slot (oldest); long.MaxValue when empty. Volatile so the governor
        //can read it without taking this instance's lock when choosing an eviction victim.
        long oldestTouchTick = long.MaxValue;

        public CachingStream(Stream baseStream, IReadSuggestor? readSuggestor, string name, MemoryGovernor? governor = null)
        {
            BaseStream = baseStream;
            ReadSuggestor = readSuggestor;
            this.name = name;
            this.governor = governor ?? MemoryGovernor.Default;

            //The concurrent single-flight path is used only when the base stream can be read positionally
            //(so misses decode in parallel via independent cursors) and a read suggestor supplies
            //deterministic, aligned spans to key and coalesce on - i.e. the zstd/gz/xz/bzip2 serving paths.
            //Every other source (restart streams, SubStream) keeps the legacy single-decode-lock path.
            concurrent = baseStream is IPositionalReader && readSuggestor != null;

            this.governor.Register(this);
        }

        readonly bool concurrent;

        // ---- concurrent path state -------------------------------------------------------------------
        readonly object mapLock = new();
        readonly Dictionary<long, SpanSlot> spanMap = [];
        readonly LinkedList<SpanSlot> lru = new();

        // ---- legacy path state -----------------------------------------------------------------------
        readonly object listLock = new();     //guards `cache` (short)
        readonly object decodeLock = new();   //serialises the stateful BaseStream seek+read (can be minutes)
        readonly List<CacheEntry> cache = [];

        //Set by a caller that holds a scarce native worker across the read (see PooledNativeItemStream), so
        //the coalescing wait below won't pin that worker. Thread-static: the read runs synchronously on one thread.
        [ThreadStatic] static bool tSuppressCoalesceWait;
        public static bool SuppressCoalesceWait { get => tSuppressCoalesceWait; set => tSuppressCoalesceWait = value; }

        //Coarse global progress signal for DokanVFS's read watchdog: bumped whenever ANY span is decoded.
        //The watchdog extends a slow read's Dokan deadline as long as this keeps advancing (gh #87).
        public static long DecodeProgress;

        //One span in the concurrent cache. Block==null while a decode is in flight; set once ready.
        sealed class SpanSlot(long start, long end)
        {
            public readonly long Start = start;
            public long End = end;                    //set to Start + bytesDecoded on publish
            public NativeBlock? Block;                //null while in flight; the decoded native buffer once ready
            public long LastTouchTick;
            public LinkedListNode<SpanSlot>? Node;    //LRU node (null while in flight)
            public readonly ManualResetEventSlim Ready = new(false);
        }

        public override void Flush() => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            //Stream.Read owns the single mutable cursor; the actual work is positional (ReadAt), so
            //parallel readers of the same cache go through ReadAt directly and never touch this position.
            var n = ReadAt(position, buffer, offset, count);
            position += n;
            return n;
        }

        //Absolute positional read: carries its own position, touches no shared cursor.
        public int ReadAt(long readPosition, byte[] buffer, int offset, int count)
        {
            return concurrent
                ? ReadAtConcurrent(readPosition, buffer, offset, count)
                : ReadAtLegacy(readPosition, buffer, offset, count);
        }

        // ---- concurrent path -------------------------------------------------------------------------

        int ReadAtConcurrent(long readPosition, byte[] buffer, int offset, int count)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CachingStream));
            if (count <= 0 || readPosition >= Length) return 0;

            var (spanStart, spanEnd) = ReadSuggestor!.GetRecommendation(readPosition);
            ThrowIfSpanMissesPosition(spanStart, spanEnd, readPosition);   //EOF was handled above, so an empty span is a bug too

            while (true)
            {
                SpanSlot slot;
                bool iOwn = false;
                lock (mapLock)
                {
                    if (disposed) throw new ObjectDisposedException(nameof(CachingStream));
                    if (spanMap.TryGetValue(spanStart, out var existing))
                    {
                        if (existing.Block != null)
                        {
                            //HIT - copy out under the lock so eviction can't free this block mid-copy
                            TouchLru(existing);
                            governor.CountHit();
                            return CopyOut(existing, readPosition, buffer, offset, count);
                        }
                        slot = existing;   //in flight
                    }
                    else
                    {
                        //clean miss: we own the decode. Publish an in-flight slot so others can coalesce.
                        slot = new SpanSlot(spanStart, spanEnd);
                        spanMap[spanStart] = slot;
                        iOwn = true;
                    }
                }

                if (!iOwn)
                {
                    if (tSuppressCoalesceWait)
                    {
                        //Serving path (holding a pool worker): NEVER block here. Decode our own copy, serve
                        //it, drop it - the owner will cache it. Bounded redundant decode instead of pinning a
                        //worker on a wait (which starves the pool and corrupts under a client-kill burst).
                        return DecodeServeAndDrop(spanStart, spanEnd, readPosition, buffer, offset, count);
                    }
                    //Mount-time parallel open (no pool worker): coalesce onto the owner's decode (L9 lockstep).
                    slot.Ready.Wait();
                    continue;
                }

                //Owner: decode with NO lock held, publish under the lock.
                governor.CountMiss();
                int spanLen = (int)(spanEnd - spanStart);
                NativeBlock? block = null;
                byte[]? staging = null;
                bool published = false;
                try
                {
                    governor.Admit(spanLen);                          //reserve BEFORE allocating; may evict globally (never this in-flight slot)
                    staging = Buffers.BufferPool.Rent(spanLen);
                    int got = ((IPositionalReader)BaseStream).ReadAt(spanStart, staging, 0, spanLen);
                    Interlocked.Increment(ref DecodeProgress);        //watchdog progress signal
                    ThrowIfShortDecode(spanStart, spanEnd, got);

                    block = NativeBlock.AllocateReserved(got, governor);   //consumes `got` of the reservation
                    governor.Release(spanLen - got);                       //release the unused tail; block now owns `got`
                    block.CopyFrom(staging, 0, got);

                    int n;
                    lock (mapLock)
                    {
                        if (disposed) throw new ObjectDisposedException(nameof(CachingStream));
                        slot.Block = block;
                        slot.End = spanStart + got;
                        slot.LastTouchTick = Environment.TickCount64;
                        slot.Node = lru.AddFirst(slot);
                        cachedBytes += got;
                        published = true;
                        UpdateOldest();
                        n = CopyOut(slot, readPosition, buffer, offset, count);
                    }
                    slot.Ready.Set();   //publish complete: wake any coalescing waiters
                    return n;
                }
                catch
                {
                    lock (mapLock) { spanMap.Remove(spanStart); }
                    slot.Ready.Set();
                    if (!published)
                    {
                        if (block != null) block.Free();          //releases the block's `got`
                        else governor.Release(spanLen);           //reservation never consumed by a block
                    }
                    throw;
                }
                finally
                {
                    if (staging != null) Buffers.BufferPool.Return(staging);
                }
            }
        }

        //Decode a span into a private pooled buffer, serve the requested bytes, return it - no caching, no
        //lock, no blocking, no governor reservation (transient). Used by the serving path when the span is
        //already being decoded by another reader (so we must not wait while holding a worker).
        int DecodeServeAndDrop(long spanStart, long spanEnd, long readPosition, byte[] buffer, int offset, int count)
        {
            governor.CountRedecode();
            int spanLen = (int)(spanEnd - spanStart);
            byte[] buff = Buffers.BufferPool.Rent(spanLen);
            try
            {
                int got = ((IPositionalReader)BaseStream).ReadAt(spanStart, buff, 0, spanLen);
                Interlocked.Increment(ref DecodeProgress);
                ThrowIfShortDecode(spanStart, spanEnd, got);   //returning 0 here would also read as a false end-of-file
                long bytesLeft = (spanStart + got) - readPosition;
                if (bytesLeft <= 0) return 0;
                int toCopy = (int)Math.Min(count, bytesLeft);
                Array.Copy(buff, readPosition - spanStart, buffer, offset, toCopy);   //private buffer - no lock needed
                return toCopy;
            }
            finally { Buffers.BufferPool.Return(buff); }
        }

        //Every read is served from the span its suggestor recommends, so that span must contain the position.
        //One that ends before it would be served as a false end-of-file (CopyOut finds no bytes left), and one that
        //starts after it as an out-of-range copy. Nothing validated this (gh review F38).
        static void ThrowIfSpanMissesPosition(long spanStart, long spanEnd, long readPosition)
        {
            if (readPosition < spanStart || readPosition >= spanEnd)
            {
                throw new IOException($"The read suggestor recommended {spanStart:N0}-{spanEnd:N0}, which does not contain position {readPosition:N0}");
            }
        }

        //ReadAt loops until the span is full or the source ends, so fewer bytes than the span means the source
        //ended early. A slot is keyed by its span start and answers EVERY read inside the span, so caching a
        //truncated slot would serve the missing tail as end-of-file for the life of the cache. Fail loudly instead:
        //this read errors, nothing is cached, and the next read of the span retries (gh review F15).
        void ThrowIfShortDecode(long spanStart, long spanEnd, int got)
        {
            long end = spanStart + got;
            if (got <= 0 || (end < spanEnd && end < Length))
            {
                throw new IOException($"Short decode for span {spanStart:N0}-{spanEnd:N0}: got {got:N0} of {spanEnd - spanStart:N0} bytes before the end of the stream");
            }
        }

        //Copies from a ready slot into the caller's buffer. MUST be called under mapLock: eviction (also
        //under mapLock) frees slot.Block, so a copy from a block referenced outside the lock could read
        //freed memory (an access violation) or another reader's bytes.
        int CopyOut(SpanSlot slot, long readPosition, byte[] buffer, int offset, int count)
        {
            long bytesLeft = slot.End - readPosition;
            if (bytesLeft <= 0) return 0;
            int toCopy = (int)Math.Min(count, bytesLeft);
            long delta = readPosition - slot.Start;
            slot.Block!.CopyTo(delta, buffer, offset, toCopy);
            return toCopy;
        }

        //Moves a hit slot to the LRU front. Under mapLock.
        void TouchLru(SpanSlot slot)
        {
            slot.LastTouchTick = Environment.TickCount64;
            if (slot.Node != null && !ReferenceEquals(lru.First, slot.Node))
            {
                lru.Remove(slot.Node);
                slot.Node = lru.AddFirst(slot);
                UpdateOldest();
            }
        }

        //Recomputes oldestTouchTick from the current LRU/list tail. Under the instance lock.
        void UpdateOldest()
        {
            long t = concurrent
                ? (lru.Last?.Value.LastTouchTick ?? long.MaxValue)
                : (cache.Count > 0 ? cache[^1].LastTouchTick : long.MaxValue);
            Volatile.Write(ref oldestTouchTick, t);
        }

        // ---- legacy path (non-positional base stream) ------------------------------------------------

        int ReadAtLegacy(long readPosition, byte[] buffer, int offset, int count)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CachingStream));
            if (count <= 0 || readPosition >= Length) return 0;   //past EOF the span below would be negative and reach Admit/Rent (gh review F15)

            //Fast hit under the short list lock (never behind a decode).
            lock (listLock)
            {
                var hit = FindLegacy(readPosition);
                if (hit != null) { TouchLegacy(hit); governor.CountHit(); return CopyLegacy(hit, readPosition, buffer, offset, count); }
            }

            var (start, toRead) = GetLegacySpan(readPosition, count);
            if (toRead == 0) return 0;

            governor.CountMiss();
            governor.Admit(toRead);

            lock (decodeLock)   //serialises the stateful BaseStream; a minutes-long restart seek can't stall a hit or the governor
            {
                //Another thread may have filled this span while we waited for decodeLock.
                lock (listLock)
                {
                    var hit = FindLegacy(readPosition);
                    if (hit != null) { governor.Release(toRead); TouchLegacy(hit); return CopyLegacy(hit, readPosition, buffer, offset, count); }
                }

                NativeBlock? block = null;
                byte[]? staging = null;
                bool published = false;
                try
                {
                    staging = Buffers.BufferPool.Rent(toRead);
                    BaseStream.Seek(start, SeekOrigin.Begin);
                    int bytesRead = BaseStream.Read(staging, 0, toRead);
                    Interlocked.Increment(ref DecodeProgress);
                    if (bytesRead == 0) throw new Exception($"No bytes read despite recommendation at {start:N0} (len {toRead:N0})");

                    block = NativeBlock.AllocateReserved(bytesRead, governor);
                    governor.Release(toRead - bytesRead);
                    block.CopyFrom(staging, 0, bytesRead);

                    int n;
                    lock (listLock)
                    {
                        if (disposed) throw new ObjectDisposedException(nameof(CachingStream));
                        var entry = new CacheEntry(start, start + bytesRead, block) { LastTouchTick = Environment.TickCount64 };
                        cache.Insert(0, entry);
                        cachedBytes += bytesRead;
                        published = true;
                        UpdateOldest();
                        n = CopyLegacy(entry, readPosition, buffer, offset, count);
                    }
                    return n;
                }
                catch
                {
                    if (!published)
                    {
                        if (block != null) block.Free();
                        else governor.Release(toRead);
                    }
                    throw;
                }
                finally
                {
                    if (staging != null) Buffers.BufferPool.Return(staging);
                }
            }
        }

        //linear scan rather than FirstOrDefault, to avoid a per-read closure allocation. Newest first, so a
        //hot entry is near the front. Caller holds listLock.
        CacheEntry? FindLegacy(long pos)
        {
            for (int i = 0; i < cache.Count; i++)
            {
                var e = cache[i];
                if (pos >= e.Start && pos < e.End) return e;
            }
            return null;
        }

        void TouchLegacy(CacheEntry entry)
        {
            entry.LastTouchTick = Environment.TickCount64;
            if (!ReferenceEquals(cache[0], entry)) { cache.Remove(entry); cache.Insert(0, entry); }
            UpdateOldest();
        }

        int CopyLegacy(CacheEntry entry, long readPosition, byte[] buffer, int offset, int count)
        {
            var bytesLeft = entry.End - readPosition;
            var toCopy = (int)Math.Min(count, bytesLeft);
            if (toCopy <= 0) return 0;
            entry.Block.CopyTo(readPosition - entry.Start, buffer, offset, toCopy);
            return toCopy;
        }

        //Stateless recommendation for the legacy path (mirrors the old ReadInternal miss logic).
        (long start, int toRead) GetLegacySpan(long readPosition, int count)
        {
            (long Start, long End) rec;
            if (ReadSuggestor == null)
            {
                var to = Math.Min(Math.Max(readPosition + count, readPosition + 1024 * 1024), Length);
                rec = (readPosition, to);
            }
            else
            {
                rec = ReadSuggestor.GetRecommendation(readPosition);
            }
            if (rec.Start == -1 || rec.End == -1)
                throw new Exception($"Could not get recommendation for reading {count:N0} bytes from position {readPosition:N0}");
            ThrowIfSpanMissesPosition(rec.Start, rec.End, readPosition);

            //recommendations can exceed what fits in an array - trim toward the read position
            var maxReadSize = Math.Min(int.MaxValue, Array.MaxLength);
            if (rec.End - rec.Start > maxReadSize)
            {
                if (readPosition < rec.Start + maxReadSize) rec.End = rec.Start + maxReadSize;
                else if (readPosition > rec.End - maxReadSize) rec.Start = rec.End - maxReadSize;
                else { rec.Start = readPosition; rec.End = readPosition + Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER; }
            }
            return (rec.Start, (int)(rec.End - rec.Start));
        }

        // ---- IGovernedCache --------------------------------------------------------------------------

        public string Name => name;
        public long CachedBytes => Interlocked.Read(ref cachedBytes);
        public int SlotCount => concurrent ? lru.Count : cache.Count;
        public long OldestTouchTick => Volatile.Read(ref oldestTouchTick);

        public bool TryEvict(long wantBytes, out long freedBytes)
        {
            freedBytes = 0;
            if (concurrent)
            {
                lock (mapLock)
                {
                    while (freedBytes < wantBytes && lru.Count > MemoryGovernor.FloorSlots && lru.Last is { } tail)
                    {
                        var victim = tail.Value;
                        lru.RemoveLast();
                        spanMap.Remove(victim.Start);
                        long len = victim.End - victim.Start;
                        cachedBytes -= len;
                        freedBytes += len;
                        victim.Block?.Free();
                        victim.Block = null;
                        victim.Node = null;
                    }
                    UpdateOldest();
                }
            }
            else
            {
                lock (listLock)
                {
                    while (freedBytes < wantBytes && cache.Count > MemoryGovernor.FloorSlots)
                    {
                        var victim = cache[^1];
                        cache.RemoveAt(cache.Count - 1);
                        long len = victim.End - victim.Start;
                        cachedBytes -= len;
                        freedBytes += len;
                        victim.Block.Free();
                    }
                    UpdateOldest();
                }
            }
            return freedBytes > 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => Length + offset,
                _ => position,
            };
            return position;
        }

        protected override void Dispose(bool disposing)
        {
            if (concurrent)
            {
                lock (mapLock)
                {
                    if (!disposed)
                    {
                        disposed = true;
                        foreach (var slot in lru) { slot.Block?.Free(); slot.Block = null; }
                        lru.Clear();
                        spanMap.Clear();
                        cachedBytes = 0;
                        UpdateOldest();
                    }
                }
            }
            else
            {
                lock (listLock)
                {
                    if (!disposed)
                    {
                        disposed = true;
                        foreach (var entry in cache) entry.Block.Free();
                        cache.Clear();
                        cachedBytes = 0;
                        UpdateOldest();
                    }
                }
            }
            governor.Unregister(this);   //outside the instance lock (registryLock > instance lock)
            base.Dispose(disposing);
        }
    }

    //One cached span on the legacy path. Backed by native memory, exactly like a concurrent SpanSlot.
    public sealed class CacheEntry(long start, long end, NativeBlock block)
    {
        public long Start = start;
        public long End = end;
        public long Length => End - Start;
        public long LastTouchTick;
        public NativeBlock Block = block;

        public override string ToString() => $"{Start:N0} - {End:N0}";
    }
}
