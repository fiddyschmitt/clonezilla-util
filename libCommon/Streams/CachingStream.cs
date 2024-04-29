using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Serilog;

namespace libCommon.Streams
{
    public class CachingStream : Stream
    {
        public CachingStream(Stream baseStream, IReadSuggestor? readSuggestor, EnumCacheType cacheType, int cacheLimitValue, List<CacheEntry>? precapturedCache)
        {
            BaseStream = baseStream;
            ReadSuggestor = readSuggestor;    //gives us insight into the most optimal way to read from the underyling stream
            CacheType = cacheType;
            CacheLimitValue = cacheLimitValue;

            cache = precapturedCache ?? new List<CacheEntry>();
        }

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
        public Stream BaseStream { get; }
        public IReadSuggestor? ReadSuggestor { get; }
        public int BufferSize { get; set; }
        public EnumCacheType CacheType { get; set; }
        public int CacheLimitValue { get; set; }

        readonly List<CacheEntry> cache;

        public IList<CacheEntry> GetCacheContents()
        {
            var result = cache.AsReadOnly();
            return result;
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var cacheEntry = cache.FirstOrDefault(entry => Position >= entry.Start && Position < entry.End);

            if (cacheEntry == null)
            {
                Log.Debug($"Cache miss: {Position:N0}");

                (long Start, long End) recommendedRead;
                if (ReadSuggestor == null)
                {
                    recommendedRead = (Position, Position + count);
                }
                else
                {
                    recommendedRead = ReadSuggestor.GetRecommendation(Position);
                }

                if (recommendedRead.Start == -1 || recommendedRead.End == -1)
                {
                    throw new Exception($"Could not get recommendation for reading {count:N0} bytes from position {Position:N0}");
                }

                Log.Debug($"Want to read from {Position:N0} to {Position + count:N0}. Was recommended to read {(recommendedRead.End - recommendedRead.Start).BytesToString()} from position {recommendedRead.Start:N0} to {recommendedRead.End:N0}");

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
                            recommendedRead.End = Position + Buffers.ARBITARY_MEDIUM_SIZE_BUFFER;
                        }
                    }
                }
                toReadLong = recommendedRead.End - recommendedRead.Start;

                int toRead = (int)toReadLong;

                if (toRead == 0)
                {
                    return 0;
                }

                var buff = new byte[toRead];

                BaseStream.Seek(recommendedRead.Start, SeekOrigin.Begin);
                var bytesRead = BaseStream.Read(buff, 0, toRead);

                if (bytesRead == 0)
                {
                    throw new Exception($"No bytes read despite recommendation of {recommendedRead.Start:N0} - {recommendedRead.End:N0}");
                }

                if (bytesRead < toRead)
                {
                    Array.Resize(ref buff, bytesRead);
                }

                cacheEntry = new CacheEntry(recommendedRead.Start, recommendedRead.Start + bytesRead, buff);

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
                            cache.RemoveAt(cache.Count - 1);
                        }
                        addToCache = true;
                        break;

                    case EnumCacheType.LimitByRAMUsage:

                        var newEntrySizeInMegabytes = (int)(cacheEntry.Length / (double)(1024 * 1024));

                        while (true)
                        {
                            var currentCacheSizeInBytes = cache.Sum(c => cacheEntry.Length);
                            var currentCacheSizeInMegabytes = (int)(currentCacheSizeInBytes / (double)(1024 * 1024));

                            if (newEntrySizeInMegabytes > CacheLimitValue)
                            {
                                addToCache = false;
                                break;
                            }

                            if (currentCacheSizeInMegabytes + newEntrySizeInMegabytes <= CacheLimitValue)
                            {
                                addToCache = true;
                                break;
                            }
                            else
                            {
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
                }
            }
            else
            {
                Log.Debug($"Cache hit: {Position:N0}");

                //move it to the beginning of the cache, to keep it fresh
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

            return bytesToRead;
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
                    position = Length - offset;
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
            cache.Clear();
        }
    }

    [Serializable]
    public class CacheEntry
    {
        public long Start;
        public long End;
        public long Length => End - Start;
        public byte[] Content;

        public CacheEntry(long start, long end, byte[] content)
        {
            Start = start;
            End = end;
            Content = content;
        }

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
