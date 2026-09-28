using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace libCommon
{
    public static class Buffers
    {

        public const int ARBITRARY_SMALL_SIZE_BUFFER = 1 * 1024 * 1024;
        public const int ARBITRARY_MEDIUM_SIZE_BUFFER = 5 * ARBITRARY_SMALL_SIZE_BUFFER;
        //Environment.Is64BitProcess is constant for the life of the process, so resolve these once at static init
        //instead of branching on every access (these are read on hot buffer-allocation paths).
        public static readonly int ARBITRARY_LARGE_SIZE_BUFFER = Environment.Is64BitProcess
            ? ARBITRARY_MEDIUM_SIZE_BUFFER * 10
            : ARBITRARY_MEDIUM_SIZE_BUFFER;

        public static readonly int ARBITRARY_HUGE_SIZE_BUFFER = Environment.Is64BitProcess
            ? ARBITRARY_LARGE_SIZE_BUFFER * 10
            : ARBITRARY_MEDIUM_SIZE_BUFFER;

        //Initialised to something big, because otherwise it defaults to 1MB and smaller.
        //See: https://adamsitnik.com/Array-Pool/
        //Always remember to return the array back into the pool.
        //Never trust buffer.Length
        //
        //maxArraysPerBucket lowered 50 -> 16: these arrays are now only TRANSIENT staging for the
        //native-memory cache slots (decode into staging, memcpy into a NativeBlock, return staging), so the
        //pool only has to cover the peak concurrent stagers (~8 serving workers + 8 mount-time opens). At 50
        //the 32/64 MB buckets alone could retain multiple GB that never trims (ConfigurableArrayPool has no
        //Gen2 trim). The wrapper below exposes the outstanding rented bytes as governor telemetry (`pool out`).
        public static readonly CountedArrayPool BufferPool = new(ArrayPool<byte>.Create(ARBITRARY_LARGE_SIZE_BUFFER + 1, 16));

        /// <summary>Bytes currently rented out of <see cref="BufferPool"/> (staging in flight).</summary>
        public static long RentedOutBytes => BufferPool.RentedOutBytes;
    }

    /// <summary>An ArrayPool that tracks outstanding rented capacity, so the memory governor can report
    /// how much transient staging is in flight. Delegates everything else to the wrapped pool.</summary>
    public sealed class CountedArrayPool(ArrayPool<byte> inner) : ArrayPool<byte>
    {
        long rentedOut;
        public long RentedOutBytes => Interlocked.Read(ref rentedOut);

        public override byte[] Rent(int minimumLength)
        {
            var array = inner.Rent(minimumLength);
            Interlocked.Add(ref rentedOut, array.Length);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            if (array != null) Interlocked.Add(ref rentedOut, -array.Length);
            inner.Return(array!, clearArray);
        }
    }
}
