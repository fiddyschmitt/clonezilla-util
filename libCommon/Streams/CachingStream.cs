using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;

namespace libCommon.Streams
{
    public class CachingStream(Stream baseStream, IReadSuggestor? readSuggestor, EnumCacheType cacheType, int cacheLimitValue, List<CacheEntry>? precapturedCache) : Stream
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
            //the cache list is not safe to mutate concurrently; callers may serve multiple readers
            lock (cacheLock)
            {
                return ReadInternal(buffer, offset, count);
            }
        }

        int ReadInternal(byte[] buffer, int offset, int count)
        {
            CacheEntry? servedEntryToReturn = null;

            //linear scan rather than FirstOrDefault, to avoid allocating a this-capturing
            //closure on every read. The cache is LRU-ordered (newest first), so a hot entry
            //is found near the front.
            var pos = Position;
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
                    var to = Math.Max(Position + count, Position + 1024 * 1024);
                    to = Math.Min(to, Length);
                    recommendedRead = (Position, to);
                }
                else
                {
                    recommendedRead = ReadSuggestor.GetRecommendation(Position);
                }

                if (recommendedRead.Start == -1 || recommendedRead.End == -1)
                {
                    throw new Exception($"Could not get recommendation for reading {count:N0} bytes from position {Position:N0}");
                }

                var maxReadSize = Math.Min(int.MaxValue, Array.MaxLength);

                //Recommendations can be larger than what can be stored in an array. Let's trim it down to size if required
                var toReadLong = recommendedRead.End - recommendedRead.Start;
                if (toReadLong > maxReadSize)
                {
                    if (Position < (recommendedRead.Start + maxReadSize))
                    {
                        //Let's bring down the recommend end
                        recommendedRead.End = recommendedRead.Start + maxReadSize;
                    }
                    else
                    {
                        if (Position > (recommendedRead.End - maxReadSize))
                        {
                            //Let's bring up the recommended start
                            recommendedRead.Start = recommendedRead.End - maxReadSize;
                        }
                        else
                        {
                            recommendedRead.Start = Position;
                            recommendedRead.End = Position + Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER;
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

            var bytesLeftInThisRange = cacheEntry.End - Position;

            var bytesToRead = (int)Math.Min(count, bytesLeftInThisRange);

            if (bytesToRead == 0)
            {
                throw new Exception($"Doing a zero-byte read");
            }

            var deltaFromBeginningOfRange = Position - cacheEntry.Start;
            if (deltaFromBeginningOfRange < 0)
            {
                throw new Exception("deltaFromBeginningOfRange < 0");
            }

            Array.Copy(cacheEntry.Content, deltaFromBeginningOfRange, buffer, offset, bytesToRead);

            Position += bytesToRead;

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
            lock (cacheLock)
            {
                foreach (var entry in cache)
                {
                    ReturnToPoolIfPooled(entry);
                }
                cache.Clear();
                currentCacheSizeBytes = 0;

                if (countedAsRamLimited && !closed)
                {
                    closed = true;
                    System.Threading.Interlocked.Decrement(ref liveRamLimitedCaches);
                }
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
