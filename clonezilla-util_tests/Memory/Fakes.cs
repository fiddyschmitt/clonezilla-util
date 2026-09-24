using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using libCommon.Memory;
using libCommon.Streams;

namespace clonezilla_util_tests.Memory
{
    /// <summary>A memory sampler the test drives directly. Change <see cref="Avail"/>, then call
    /// governor.ForceSample() (or Tick) to make the governor see it.</summary>
    sealed class FakeSampler
    {
        public long Total = 64L * 1024 * 1024 * 1024;
        public long Avail;

        public FakeSampler(long avail) => Avail = avail;

        public MemorySample? Sample(long liveNative) => new MemorySample
        {
            TotalPhys = Total,
            AvailPhys = Volatile.Read(ref Avail),
            AvailPageFile = long.MaxValue,
            LiveNativeAtSample = liveNative,
            Tick = Environment.TickCount64,
        };
    }

    /// <summary>A minimal governed cache: each slot is just a (size, touchTick) pair. Admits through the
    /// governor and releases on eviction, so the governor's liveNativeBytes tracks exactly.</summary>
    sealed class FakeCache : IGovernedCache
    {
        readonly MemoryGovernor gov;
        readonly object gate = new();
        readonly List<long> sizes = new();
        readonly List<long> touch = new();

        public string Name { get; }

        public FakeCache(MemoryGovernor gov, string name) { this.gov = gov; Name = name; gov.Register(this); }

        /// <summary>Reserve and add a slot. Admit may evict other caches (or this one) to make room.</summary>
        public void Add(long bytes, long touchTick)
        {
            gov.Admit(bytes);
            lock (gate) { sizes.Add(bytes); touch.Add(touchTick); }
        }

        public long CachedBytes { get { lock (gate) return sizes.Sum(); } }
        public int SlotCount { get { lock (gate) return sizes.Count; } }
        public long OldestTouchTick { get { lock (gate) return sizes.Count == 0 ? long.MaxValue : touch.Min(); } }

        public bool TryEvict(long wantBytes, out long freedBytes)
        {
            freedBytes = 0;
            lock (gate)
            {
                while (freedBytes < wantBytes && sizes.Count > MemoryGovernor.FloorSlots)
                {
                    int oldest = 0;
                    for (int i = 1; i < touch.Count; i++) if (touch[i] < touch[oldest]) oldest = i;
                    freedBytes += sizes[oldest];
                    gov.Release(sizes[oldest]);
                    sizes.RemoveAt(oldest);
                    touch.RemoveAt(oldest);
                }
            }
            return freedBytes > 0;
        }
    }

    /// <summary>Positional (concurrent-path) source with deterministic content, 64 KB spans, and a
    /// per-span decode counter. Optionally delays or throws on the Nth decode.</summary>
    sealed class FakePositionalSource : Stream, IPositionalReader, IReadSuggestor
    {
        public const int SpanSize = 64 * 1024;
        readonly long length;
        int decodeCounter;

        public int DecodeDelayMs;
        public int ThrowEveryNth;   // 0 = never
        public readonly ConcurrentDictionary<long, int> DecodesPerSpan = new();

        public FakePositionalSource(long length) => this.length = length;

        public static byte ByteAt(long pos) { unchecked { ulong x = (ulong)pos * 0x9E3779B97F4A7C15UL; return (byte)(x >> 56); } }

        public int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            if (position >= length) return 0;
            int n = (int)Math.Min(count, length - position);
            long spanStart = position - (position % SpanSize);
            DecodesPerSpan.AddOrUpdate(spanStart, 1, (_, v) => v + 1);
            int idx = Interlocked.Increment(ref decodeCounter);
            if (ThrowEveryNth > 0 && idx % ThrowEveryNth == 0) throw new IOException("injected decode failure");
            if (DecodeDelayMs > 0) Thread.Sleep(DecodeDelayMs);
            for (int i = 0; i < n; i++) buffer[offset + i] = ByteAt(position + i);
            return n;
        }

        public (long Start, long End) GetRecommendation(long start)
        {
            if (start >= length) return (start, start);
            long s = start - (start % SpanSize);
            return (s, Math.Min(s + SpanSize, length));
        }

        public override long Length => length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    /// <summary>Seekable-only (legacy-path) source: NOT IPositionalReader, so CachingStream serialises its
    /// decode. Same deterministic content. Supplies a 64 KB-aligned recommendation.</summary>
    sealed class FakeSeekableSource : Stream, IReadSuggestor
    {
        public const int SpanSize = 64 * 1024;
        readonly long length;
        long pos;

        public int DecodeDelayMs;
        public ManualResetEventSlim? EnteredDecode;   //set when a real decode read begins (lock-split test)
        public readonly ConcurrentDictionary<long, int> DecodesPerSpan = new();

        public FakeSeekableSource(long length) => this.length = length;

        public (long Start, long End) GetRecommendation(long start)
        {
            if (start >= length) return (start, start);
            long s = start - (start % SpanSize);
            return (s, Math.Min(s + SpanSize, length));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (pos >= length) return 0;
            int n = (int)Math.Min(count, length - pos);
            long spanStart = pos - (pos % SpanSize);
            DecodesPerSpan.AddOrUpdate(spanStart, 1, (_, v) => v + 1);
            EnteredDecode?.Set();
            if (DecodeDelayMs > 0) Thread.Sleep(DecodeDelayMs);
            for (int i = 0; i < n; i++) buffer[offset + i] = FakePositionalSource.ByteAt(pos + i);
            pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => pos + offset, SeekOrigin.End => length + offset, _ => pos };
            return pos;
        }

        public override long Position { get => pos; set => pos = value; }
        public override long Length => length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
