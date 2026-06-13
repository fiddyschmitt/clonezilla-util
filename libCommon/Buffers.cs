using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace libCommon
{
    public static class Buffers
    {

        public const int ARBITRARY_SMALL_SIZE_BUFFER = 1 * 1024 * 1024;
        public const int ARBITRARY_MEDIUM_SIZE_BUFFER = 5 * ARBITRARY_SMALL_SIZE_BUFFER;
        public static int ARBITRARY_LARGE_SIZE_BUFFER
        {
            get
            {
                if (Environment.Is64BitProcess)
                {
                    return ARBITRARY_MEDIUM_SIZE_BUFFER * 10;
                }
                else
                {
                    return ARBITRARY_MEDIUM_SIZE_BUFFER;
                }
            }
        }

        public static int ARBITRARY_HUGE_SIZE_BUFFER
        {
            get
            {
                if (Environment.Is64BitProcess)
                {
                    return ARBITRARY_LARGE_SIZE_BUFFER * 10;
                }
                else
                {
                    return ARBITRARY_MEDIUM_SIZE_BUFFER;
                }
            }
        }

        //Initialised to something big, because otherwise it defaults to 1MB and smaller.
        //See: https://adamsitnik.com/Array-Pool/
        //Always remember to return the array back into the pool.
        //Never trust buffer.Length
        public static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Create(ARBITRARY_LARGE_SIZE_BUFFER + 1, 50);
    }
}
