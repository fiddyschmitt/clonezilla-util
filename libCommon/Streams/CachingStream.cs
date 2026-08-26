using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;

namespace libCommon.Streams
{
    public class CachingStream(Stream baseStream, IReadSuggestor? readSuggestor, EnumCacheType cacheType, int cacheLimitValue, List<CacheEntry>? precapturedCache) : Stream, IPositionalReader
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => BaseStream.Length;

        long position = 0;
        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }
        public Stream BaseStream { get; } = baseStream;
        public IReadSuggestor? ReadSuggestor { get; } = readSuggestor;    //gives us insight into the most optimal way to read from the underyling stream
        public int BufferSize { get; set; }
        public EnumCacheType CacheType { get; set; } = cacheType;
        public int CacheLimitValue { get; set; } = cacheLimitValue;

        readonly List<CacheEntry> cache = precapturedCache ?? [];
        //running total of cached byte size, kept in sync at every cache mutation so the
        //LimitByRAMUsage eviction loop doesn't recompute cache.Sum(...) on each iteration
        long currentCacheSizeBytes = precapturedCache?.Sum(c => c.Length) ?? 0;

        //CacheLimitValue used to be a PER-INSTANCE budget, so mounting N partitions (each wrapped in
        //a ¼-RAM cache) could commit N×¼ of system RAM and exhaust the machine (measured: free RAM
        //fell to ~400 MB on a 3-partition mount). LimitByRAMUsage instances now split their declared
        //budget evenly: each keeps at most CacheLimitValue ÷ (live instances). All current callers
        //pass the same ¼-RAM value, so the TOTAL stays ≤ ¼ RAM regardless of partition count, with
        //no cross-instance locking (each instance still evicts only from its own LRU list). An
        //instance that is never Close()d keeps its slot reserved - that errs towards using LESS
        //memory, never more.
        static int liveRamLimitedCaches;
        readonly bool countedAsRamLimited = cacheType == EnumCacheType.LimitByRAMUsage
            && System.Threading.Interlocked.Increment(ref liveRamLimitedCaches) > 0;
        bool closed;

        //The concurrent single-flight path is used only when the base stream can be read positionally (so
        //misses decode in parallel via independent cursors) and a read suggestor supplies deterministic,
        //aligned spans to key and coalesce on - i.e. exactly the zstd serving path. Every other codec
        //(whose seekable stream is not IPositionalReader) keeps the legacy single-lock path unchanged.
        readonly bool concurrent = baseStream is IPositionalReader && readSuggestor != null
            && cacheType == EnumCacheType.LimitByRAMUsage;

        //Concurrent cache (used when `concurrent`). The map keys a span-start to a slot; an in-flight slot
        //has Content==null. mapLock guards map + lru + currentCacheSizeBytes + copy-out, and is NEVER held
        //across a decode - misses on different spans decode in parallel.
        //
        //Coalescing (a second reader of an in-flight span waiting on the first) is gated by
        //SuppressCoalesceWait. When a reader holds a scarce native 7z worker (the pool serving path), it
        //MUST NOT block waiting here: waiters would pin workers and starve the pool, which surfaced as Dokan
        //read timeouts and cross-file corruption when a client died mid-read (TEST_ANALYSIS.md). Such readers
        //instead decode their own copy and drop it. The mount-time parallel worker-open holds no pool worker,
        //leaves the flag false, and keeps the lockstep coalescing the parallel open relies on (Lead L9) -
        //without it, 4 workers each re-decode the whole ~1 GB $MFT.
        readonly object mapLock = new();
        readonly Dictionary<long, SpanSlot> spanMap = [];
        readonly LinkedList<SpanSlot> lru = new();

        //Set by a caller that holds a scarce native worker across the read (see PooledNativeItemStream), so
        //the coalescing wait below won't pin that worker. Thread-static: the read runs synchronously on one thread.
        [ThreadStatic] static bool tSuppressCoalesceWait;
        public static bool SuppressCoalesceWait { get => tSuppressCoalesceWait; set => tSuppressCoalesceWait = value; }

        //Coarse global progress signal for DokanVFS's read watchdog: bumped whenever ANY span is decoded.
        //The watchdog extends a slow read's Dokan deadline as long as this keeps advancing (the mount is
        //doing decode work), and abandons a read only once it stops advancing for a while (the mount is
        //genuinely stuck). This lets a legitimately-slow read - e.g. a heavily-fragmented log whose clusters
        //scatter across many spans, seconds of decode each - run to completion instead of being killed at a
        //fixed elapsed cap and left to hang an external tool (gh #87).
        public static long DecodeProgress;

        //One span in the concurrent cache. Content==null while a decode is in flight; set once ready.
        sealed class SpanSlot(long start, long end)
        {
            public readonly long Start = start;
            public long End = end;                    //set to Start + bytesDecoded on publish
            public byte[]? Content;                   //null while in flight; the decoded buffer once ready
            public LinkedListNode<SpanSlot>? Node;    //LRU node (null while in flight)
            public readonly System.Threading.ManualResetEventSlim Ready = new(false);
        }

        public IList<CacheEntry> GetCacheContents()
        {
            var result = cache.AsReadOnly();
            return result;
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        readonly object cacheLock = new();

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
            if (concurrent)
            {
                return ReadAtConcurrent(readPosition, buffer, offset, count);
            }

            //Legacy path: the whole miss (including the decode) is serialized on cacheLock. Used when the
            //base stream has no positional API (non-zstd codecs) or for non-RAM cache types.
            lock (cacheLock)
            {
                return ReadInternal(readPosition, buffer, offset, count);
            }
        }

        //Concurrent read. A hit is a brief locked memcpy. A clean miss becomes the span's owner: it decodes
        //with no lock held, then publishes (caches) it. A second reader that finds the span already in flight
        //either waits for the owner (coalesce - mount-time, no worker held) or, if it holds a scarce worker
        //(serving path, SuppressCoalesceWait), decodes its own copy and drops it rather than block.
        int ReadAtConcurrent(long readPosition, byte[] buffer, int offset, int count)
        {
            if (count <= 0 || readPosition >= Length) return 0;

            var (spanStart, spanEnd) = ReadSuggestor!.GetRecommendation(readPosition);
            if (spanEnd <= spanStart) return 0;   //at/after EOF

            while (true)
            {
                SpanSlot slot;
                bool iOwn = false;
                lock (mapLock)
                {
                    if (spanMap.TryGetValue(spanStart, out var existing))
                    {
                        if (existing.Content != null)
                        {
                            //HIT - copy out under the lock so eviction can't return this buffer mid-copy
                            TouchLru(existing);
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
                        //it, drop it - the owner will cache it. Bounded redundant decode instead of pinning
                        //a worker on a wait (which starves the pool and corrupts under a client-kill burst).
                        return DecodeServeAndDrop(spanStart, spanEnd, readPosition, buffer, offset, count);
                    }
                    //Mount-time parallel open (no pool worker): coalesce onto the owner's decode (L9 lockstep).
                    slot.Ready.Wait();
                    continue;
                }

                //Owner: decode with NO lock held, then publish under the lock.
                byte[]? buff = null;
                try
                {
                    int spanLen = (int)(spanEnd - spanStart);
                    buff = Buffers.BufferPool.Rent(spanLen);
                    int got = ((IPositionalReader)BaseStream).ReadAt(spanStart, buff, 0, spanLen);
                    System.Threading.Interlocked.Increment(ref DecodeProgress);   //watchdog progress signal
                    if (got <= 0) throw new Exception($"No bytes decoded for span {spanStart:N0}-{spanEnd:N0}");

                    int n;
                    lock (mapLock)
                    {
                        slot.Content = buff;
                        slot.End = spanStart + got;
                        buff = null;   //ownership transferred to the slot; the finally/catch must not return it
                        InsertAndEvict(slot);
                        n = CopyOut(slot, readPosition, buffer, offset, count);
                    }
                    slot.Ready.Set();   //publish complete: wake any coalescing waiters
                    return n;
                }
                catch
                {
                    //Decode failed: drop the slot so a later read retries, wake waiters (they re-loop), and
                    //return the rented buffer if it never reached the slot.
                    lock (mapLock) { spanMap.Remove(spanStart); }
                    slot.Ready.Set();
                    if (buff != null) Buffers.BufferPool.Return(buff);
                    throw;
                }
            }
        }

        //Decode a span into a private buffer, serve the requested bytes from it, and return it to the pool -
        //no caching, no lock, no blocking. Used by the serving path when the span is already being decoded by
        //another reader (so we must not wait while holding a worker).
        int DecodeServeAndDrop(long spanStart, long spanEnd, long readPosition, byte[] buffer, int offset, int count)
        {
            int spanLen = (int)(spanEnd - spanStart);
            byte[] buff = Buffers.BufferPool.Rent(spanLen);
            try
            {
                int got = ((IPositionalReader)BaseStream).ReadAt(spanStart, buff, 0, spanLen);
                System.Threading.Interlocked.Increment(ref DecodeProgress);   //watchdog progress signal
                long bytesLeft = (spanStart + got) - readPosition;
                if (bytesLeft <= 0) return 0;
                int toCopy = (int)Math.Min(count, bytesLeft);
                Array.Copy(buff, readPosition - spanStart, buffer, offset, toCopy);   //private buffer - no lock needed
                return toCopy;
            }
            finally { Buffers.BufferPool.Return(buff); }
        }

        //Copies from a ready slot into the caller's buffer. MUST be called under mapLock: eviction (also
        //under mapLock) may return slot.Content to the pool, so copying here - never from a reference held
        //outside the lock - is what prevents cross-reader data bleed.
        int CopyOut(SpanSlot slot, long readPosition, byte[] buffer, int offset, int count)
        {
            long bytesLeft = slot.End - readPosition;
            if (bytesLeft <= 0) return 0;
            int toCopy = (int)Math.Min(count, bytesLeft);
            long delta = readPosition - slot.Start;
            Array.Copy(slot.Content!, delta, buffer, offset, toCopy);
            return toCopy;
        }

        //Publishes a freshly-decoded slot into the LRU and evicts to the RAM budget. Under mapLock.
        void InsertAndEvict(SpanSlot slot)
        {
            slot.Node = lru.AddFirst(slot);
            currentCacheSizeBytes += slot.End - slot.Start;

            //Evict LRU-tail until within this instance's share of the budget. Never evict the slot just
            //inserted (LRU-head): if a single span exceeds the budget (e.g. a deliberately tiny stress
            //budget), the cache holds just that one, temporarily over budget, until the next span pushes it out.
            long budgetBytes = (long)(CacheLimitValue / Math.Max(1, liveRamLimitedCaches)) * 1024 * 1024;
            while (currentCacheSizeBytes > budgetBytes
                   && lru.Last is { } tailNode && !ReferenceEquals(tailNode.Value, slot))
            {
                var victim = tailNode.Value;
                lru.RemoveLast();
                spanMap.Remove(victim.Start);                 //remove from the map BEFORE returning the buffer
                currentCacheSizeBytes -= victim.End - victim.Start;
                var content = victim.Content;
                victim.Content = null;
                victim.Node = null;
                if (content != null) Buffers.BufferPool.Return(content);
            }
        }

        //Moves a hit slot to the LRU front. Under mapLock.
        void TouchLru(SpanSlot slot)
        {
            if (slot.Node != null && !ReferenceEquals(lru.First, slot.Node))
            {
                lru.Remove(slot.Node);
                slot.Node = lru.AddFirst(slot);
            }
        }

        int ReadInternal(long readPosition, byte[] buffer, int offset, int count)
        {
            CacheEntry? servedEntryToReturn = null;

            //linear scan rather than FirstOrDefault, to avoid allocating a this-capturing
            //closure on every read. The cache is LRU-ordered (newest first), so a hot entry
            //is found near the front.
            var pos = readPosition;
            CacheEntry? cacheEntry = null;
            for (int i = 0; i < cache.Count; i++)
            {
                var entry = cache[i];
                if (pos >= entry.Start && pos < entry.End)
                {
                    cacheEntry = entry;
                    break;
                }
            }

            if (cacheEntry == null)
            {
                (long Start, long End) recommendedRead;
                if (ReadSuggestor == null)
                {
                    var to = Math.Max(readPosition + count, readPosition + 1024 * 1024);
                    to = Math.Min(to, Length);
                    recommendedRead = (readPosition, to);
                }
                else
                {
                    recommendedRead = ReadSuggestor.GetRecommendation(readPosition);
                }

                if (recommendedRead.Start == -1 || recommendedRead.End == -1)
                {
                    throw new Exception($"Could not get recommendation for reading {count:N0} bytes from position {readPosition:N0}");
                }

                var maxReadSize = Math.Min(int.MaxValue, Array.MaxLength);

                //Recommendations can be larger than what can be stored in an array. Let's trim it down to size if required
                var toReadLong = recommendedRead.End - recommendedRead.Start;
                if (toReadLong > maxReadSize)
                {
                    if (readPosition < (recommendedRead.Start + maxReadSize))
                    {
                        //Let's bring down the recommend end
                        recommendedRead.End = recommendedRead.Start + maxReadSize;
                    }
                    else
                    {
                        if (readPosition > (recommendedRead.End - maxReadSize))
                        {
                            //Let's bring up the recommended start
                            recommendedRead.Start = recommendedRead.End - maxReadSize;
                        }
                        else
                        {
                            recommendedRead.Start = readPosition;
                            recommendedRead.End = readPosition + Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER;
                        }
                    }
                }
                toReadLong = recommendedRead.End - recommendedRead.Start;

                int toRead = (int)toReadLong;

                if (toRead == 0)
                {
                    return 0;
                }

                //Pooled: a fresh multi-MB array per cache miss was the main source of LOH churn
                //(measured gcFrag spikes of ~2 GB during full-copy runs). Rented arrays can be larger
                //than requested; every consumer is bounded by the entry's Start/End, never by
                //Content.Length. Short reads just make the entry span smaller - no Array.Resize copy.
                var buff = Buffers.BufferPool.Rent(toRead);

                BaseStream.Seek(recommendedRead.Start, SeekOrigin.Begin);
                var bytesRead = BaseStream.Read(buff, 0, toRead);
                System.Threading.Interlocked.Increment(ref DecodeProgress);   //watchdog progress signal

                if (bytesRead == 0)
                {
                    Buffers.BufferPool.Return(buff);
                    throw new Exception($"No bytes read despite recommendation of {recommendedRead.Start:N0} - {recommendedRead.End:N0}");
                }

                cacheEntry = new CacheEntry(recommendedRead.Start, recommendedRead.Start + bytesRead, buff, pooled: true);

                //clear the cache until there's enough room
                bool addToCache;
                switch (CacheType)
                {
                    case EnumCacheType.NoCaching:
                        addToCache = false;
                        break;

                    case EnumCacheType.LimitBySegmentCount:
                        while (cache.Count >= CacheLimitValue)
                        {
                            currentCacheSizeBytes -= cache[cache.Count - 1].Length;
                            ReturnToPoolIfPooled(cache[cache.Count - 1]);
                            cache.RemoveAt(cache.Count - 1);
                        }
                        addToCache = true;
                        break;

                    case EnumCacheType.Unlimited:
                        addToCache = true;
                        break;

                    case EnumCacheType.LimitByRAMUsage:

                        //this instance's share of the declared budget (see liveRamLimitedCaches above)
                        var effectiveLimitInMegabytes = CacheLimitValue / Math.Max(1, liveRamLimitedCaches);

                        var newEntrySizeInMegabytes = (int)(cacheEntry.Length / (double)(1024 * 1024));

                        while (true)
                        {
                            var currentCacheSizeInMegabytes = (int)(currentCacheSizeBytes / (double)(1024 * 1024));

                            if (newEntrySizeInMegabytes > effectiveLimitInMegabytes)
                            {
                                addToCache = false;
                                break;
                            }

                            if (currentCacheSizeInMegabytes + newEntrySizeInMegabytes <= effectiveLimitInMegabytes)
                            {
                                addToCache = true;
                                break;
                            }
                            else
                            {
                                currentCacheSizeBytes -= cache[cache.Count - 1].Length;
                                ReturnToPoolIfPooled(cache[cache.Count - 1]);
                                cache.RemoveAt(cache.Count - 1);
                            }
                        }
                        break;

                    default:
                        addToCache = false;
                        break;
                }

                if (addToCache)
                {
                    cache.Insert(0, cacheEntry);
                    currentCacheSizeBytes += cacheEntry.Length;
                }
                else
                {
                    //fresh rented buffer that never entered the cache: serve from it, then return it
                    servedEntryToReturn = cacheEntry;
                }
            }
            else
            {
                //move it to the beginning of the cache, to keep it fresh.
                //net-zero for currentCacheSizeBytes (same entry removed then re-added).
                cache.Remove(cacheEntry);
                cache.Insert(0, cacheEntry);
            }

            var bytesLeftInThisRange = cacheEntry.End - readPosition;

            var bytesToRead = (int)Math.Min(count, bytesLeftInThisRange);

            if (bytesToRead == 0)
            {
                throw new Exception($"Doing a zero-byte read");
            }

            var deltaFromBeginningOfRange = readPosition - cacheEntry.Start;
            if (deltaFromBeginningOfRange < 0)
            {
                throw new Exception("deltaFromBeginningOfRange < 0");
            }

            Array.Copy(cacheEntry.Content, deltaFromBeginningOfRange, buffer, offset, bytesToRead);

            //no shared Position to advance - ReadAt is positional; Stream.Read advances its own cursor.

            if (servedEntryToReturn != null)
            {
                ReturnToPoolIfPooled(servedEntryToReturn);
            }

            return bytesToRead;
        }

        static void ReturnToPoolIfPooled(CacheEntry entry)
        {
            if (entry.Pooled)
            {
                Buffers.BufferPool.Return(entry.Content);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;

                case SeekOrigin.Current:
                    position += offset;
                    break;

                case SeekOrigin.End:
                    position = Length + offset;
                    break;
            }

            return position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override void Close()
        {
            if (concurrent)
            {
                lock (mapLock)
                {
                    //Drain the cached slots, returning each buffer to the pool.
                    foreach (var slot in lru)
                    {
                        if (slot.Content != null) Buffers.BufferPool.Return(slot.Content);
                        slot.Content = null;
                    }
                    lru.Clear();
                    spanMap.Clear();
                    currentCacheSizeBytes = 0;
                }
            }
            else
            {
                lock (cacheLock)
                {
                    foreach (var entry in cache)
                    {
                        ReturnToPoolIfPooled(entry);
                    }
                    cache.Clear();
                    currentCacheSizeBytes = 0;
                }
            }

            if (countedAsRamLimited && !closed)
            {
                closed = true;
                System.Threading.Interlocked.Decrement(ref liveRamLimitedCaches);
            }
        }
    }

    [Serializable]
    public class CacheEntry(long start, long end, byte[] content, bool pooled = false)
    {
        public long Start = start;
        public long End = end;
        public long Length => End - Start;

        //may be LARGER than Length (rented from a pool) - always bound access by Start/End
        public byte[] Content = content;

        //true when Content was rented from Buffers.BufferPool and must be returned on eviction
        public readonly bool Pooled = pooled;

        public override string ToString()
        {
            string result = $"{Start:N0} - {End:N0}";
            return result;
        }
    }

    public enum EnumCacheType
    {
        NoCaching,
        LimitBySegmentCount,
        LimitByRAMUsage,
        Unlimited
    }
}
