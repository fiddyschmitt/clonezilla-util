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
            var pos = position;
            var bufferPos = offset;
            var end = pos + count;
            var totalBytesRead = 0;

            while (pos < end)
            {
                var bytesToGo = end - pos;

                var cacheEntry = cache.FirstOrDefault(entry => pos >= entry.Start && pos < entry.End);

                if (cacheEntry == null)
                {
                    //Log.Information($"Cache miss: {pos}");

                    (long Start, long End) recommendedRead;
                    if (ReadSuggestor == null)
                    {
                        recommendedRead = (pos, pos + bytesToGo);
                    }
                    else
                    {
                        recommendedRead = ReadSuggestor.GetRecommendation(pos, pos + bytesToGo);
                    }

                    //Log.Information($"Want to read from {pos:N0} to {pos + bytesToGo:N0}. Was recommended to read {(recommendedRead.End - recommendedRead.Start).BytesToString()} from position {recommendedRead.Start:N0} to {recommendedRead.End:N0}");

                    var toRead = (int)Math.Min(recommendedRead.End - recommendedRead.Start, int.MaxValue);

                    if (toRead == 0)
                    {
                        break;
                    }

                    if (!Environment.Is64BitProcess)
                    {
                        toRead = Math.Min(toRead, Buffers.ARBITARY_MEDIUM_SIZE_BUFFER);
                    }


                    var buff = new byte[toRead];

                    BaseStream.Seek(recommendedRead.Start, SeekOrigin.Begin);
                    var bytesRead = BaseStream.Read(buff, 0, toRead);

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    if (bytesRead < toRead)
                    {
                        var trimmedBuff = new byte[bytesRead];
                        Array.Copy(buff, trimmedBuff, trimmedBuff.Length);
                        buff = trimmedBuff;
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
                    //Log.Information($"Cache hit: {pos:N0}");

                    //move it to the beginning of the cache, to keep it fresh
                    cache.Remove(cacheEntry);
                    cache.Insert(0, cacheEntry);
                }

                var bytesLeftInThisRange = cacheEntry.End - pos;

                if (pos >= cacheEntry.End)
                {
                    //throw new Exception($"Position {pos:N0} is at or beyond the range of this cacheEntry {cacheEntry.End:N0}");
                    //Log.Information($"Position {pos:N0} is at or beyond the range of this cacheEntry {cacheEntry.End:N0}");
                    break;
                }

                var bytesToRead = (int)Math.Min(bytesToGo, bytesLeftInThisRange);

                if (bytesToRead == 0)
                {
                    throw new Exception($"Doing a zero-byte read");
                }

                var deltaFromBeginningOfRange = pos - cacheEntry.Start;
                if (deltaFromBeginningOfRange < 0)
                {
                    throw new Exception("deltaFromBeginningOfRange < 0");
                }

                Array.Copy(cacheEntry.Content, deltaFromBeginningOfRange, buffer, bufferPos, bytesToRead);

                bufferPos += bytesToRead;
                pos += bytesToRead;
                totalBytesRead += bytesToRead;
            }

            position = pos;

            return totalBytesRead;
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
