using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace libCommon.Streams.VirtualDisks
{
    static class Checksums
    {
        static readonly uint[] Crc32Table = BuildCrc32Table();

        /// <summary>CRC-32 (IEEE 802.3), as the GUID partition table uses.</summary>
        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        /// <summary>CRC-32C (Castagnoli), as VHDX uses.</summary>
        public static uint Crc32C(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data) crc = BitOperations.Crc32C(crc, b);
            return ~crc;
        }

        static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (var i = 0u; i < table.Length; i++)
            {
                var value = i;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }

        /// <summary>
        /// A GUID derived from <paramref name="identity"/> for one <paramref name="purpose"/>, so a virtual disk's ids
        /// are the same on every mount while different partitions (and different ids of one disk) do not collide.
        /// </summary>
        public static Guid DerivedGuid(string identity, string purpose) =>
            new(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}\n{purpose}")).AsSpan(0, 16));
    }
}
