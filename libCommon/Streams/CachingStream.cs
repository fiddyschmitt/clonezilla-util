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
        public CachingStream(Stream baseStream, IReadSegmentSuggestor? readSegmentSuggestor, EnumCacheType cacheType, int cacheLimitValue, List<CacheEntry>? precapturedCache)
        {
            BaseStream = baseStream;
            ReadSegmentSuggestor = readSegmentSuggestor;    //gives us insight into the most optimal way to read from the underyling stream
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
        public IReadSegmentSuggestor? ReadSegmentSuggestor { get; }
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

        //StreamWriter readPositions = null;
        public override int Read(byte[] buffer, int offset, int count)
        {
            /*
            if (readPositions == null)
            {
                var stream = File.Open(@"E:\read_positions.txt", FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                readPositions = new StreamWriter(stream);
                readPositions.AutoFlush = true;
            }
            readPositions.WriteLine($"{position},{position + count}");
            */
            var pos = position;
            var bufferPos = offset;
            var end = pos + count;

            while (pos < end)
            {
                var bytesToGo = end - pos;

                var cacheEntry = cache.FirstOrDefault(entry => pos >= entry.Start && pos < entry.End);

                if (cacheEntry == null)
                {
                    Log.Debug("\tCache miss");

                    (long Start, long End) recommendedRead;
                    if (ReadSegmentSuggestor == null)
                    {
                        recommendedRead = (pos, pos + bytesToGo);
                    }
                    else
                    {
                        recommendedRead = ReadSegmentSuggestor.GetRecommendation(pos, pos + bytesToGo);
                    }
                    var toRead = (int)(recommendedRead.End - recommendedRead.Start);
                    Log.Debug($"\tRecommended to read {toRead.BytesToString()} from position {recommendedRead.Start:N0}");

                    var buff = new byte[toRead];

                    BaseStream.Seek(recommendedRead.Start, SeekOrigin.Begin);
                    var bytesRead = BaseStream.Read(buff, 0, toRead);

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
                                var currentCacheSizeInBytes = cache.Sum(c => (long)cacheEntry.Length);
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
                    //move it to the beginning of the cache, to keep it fresh
                    cache.Remove(cacheEntry);
                    cache.Insert(0, cacheEntry);
                }

                var bytesLeftInThisRange = cacheEntry.End - pos;
                var bytesToRead = (int)Math.Min(bytesToGo, bytesLeftInThisRange);

                var deltaFromBeginningOfRange = pos - cacheEntry.Start;
                if (deltaFromBeginningOfRange < 0)
                {
                    throw new Exception("deltaFromBeginningOfRange < 0");
                }

                Array.Copy(cacheEntry.Content, deltaFromBeginningOfRange, buffer, bufferPos, bytesToRead);

                bufferPos += bytesToRead;
                pos += bytesToRead;
            }

            position = pos;

            var totalBytesRead = count;
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
