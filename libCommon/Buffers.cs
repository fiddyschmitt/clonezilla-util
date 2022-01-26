using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace libCommon
{
    public static class Buffers
    {
        public const int ARBITARY_MEDIUM_SIZE_BUFFER = 5 * 1024 * 1024;
        public const int ARBITARY_LARGE_SIZE_BUFFER = ARBITARY_MEDIUM_SIZE_BUFFER * 10;
        public const int ARBITARY_HUGE_SIZE_BUFFER = ARBITARY_LARGE_SIZE_BUFFER * 10;

        //Initialised to something big, because otherwise it defaults to 1MB and smaller.
        //See: https://adamsitnik.com/Array-Pool/
        //Always remember to return the array back into the pool.
        //Never trust buffer.Length
        public static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Create(ARBITARY_LARGE_SIZE_BUFFER + 1, 50);
    }
}
