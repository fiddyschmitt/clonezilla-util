using System;

namespace libPartclone
{
    /// <summary>
    /// The CRC-32 partclone stores after its v2 header and after the bitmap: the standard reflected CRC-32
    /// (polynomial 0xEDB88320, initial value 0xFFFFFFFF) WITHOUT the final XOR. Verified against the test assets on
    /// 2026-09-26: for every v2 image (partclone 2020-2023 builds), the stored header and bitmap CRCs equal zlib's
    /// crc32 of the same bytes XOR 0xFFFFFFFF.
    /// </summary>
    public static class PartcloneCrc32
    {
        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc;   //no final XOR: partclone's variant
        }
    }
}
