using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace libCommon
{
    public static class Buffers
    {
        public const int SUPER_ARBITARY_MEDIUM_SIZE_BUFFER = 5_000_000;
        public const int SUPER_ARBITARY_LARGE_SIZE_BUFFER = SUPER_ARBITARY_MEDIUM_SIZE_BUFFER * 10;
        public const int SUPER_ARBITARY_HUGE_SIZE_BUFFER = SUPER_ARBITARY_LARGE_SIZE_BUFFER * 10;

        //Initialised to something big, because otherwise it defaults to 1MB and smaller.
        //See: https://adamsitnik.com/Array-Pool/
        //Always remember to return the array back into the pool.
        //Never trust buffer.Length
        public static ArrayPool<byte> BufferPool = ArrayPool<byte>.Create(SUPER_ARBITARY_LARGE_SIZE_BUFFER + 1, 50);
    }
}
