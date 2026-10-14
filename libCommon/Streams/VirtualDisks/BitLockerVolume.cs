using System;

namespace libCommon.Streams.VirtualDisks
{
    /// <summary>
    /// Recognises a BitLocker-encrypted volume by its first sector, as blkid and libbde do: either "-FVE-FS-" where a
    /// boot sector keeps its OEM name (BitLocker on Windows Vista and later), or a BitLocker To Go header - a FAT boot
    /// sector named "MSWIN4.1" that carries BitLocker's volume identifier at offset 424.
    /// </summary>
    public static class BitLockerVolume
    {
        //{4967D63B-2E29-4AD8-8399-F6A339E3D001}, the identifier a BitLocker To Go header carries
        static readonly byte[] ToGoIdentifier = new Guid("4967D63B-2E29-4AD8-8399-F6A339E3D001").ToByteArray();

        public static bool IsHeader(ReadOnlySpan<byte> firstSector)
        {
            if (firstSector.Length < 512) return false;

            var oemName = firstSector.Slice(3, 8);
            if (oemName.SequenceEqual("-FVE-FS-"u8)) return true;
            return oemName.SequenceEqual("MSWIN4.1"u8) && firstSector.Slice(424, 16).SequenceEqual(ToGoIdentifier);
        }

        /// <summary>Reads the volume's first sector; false if there is not a whole one.</summary>
        public static bool IsVolume(IPositionalReader volume)
        {
            var sector = new byte[512];
            var got = 0;
            while (got < sector.Length)
            {
                var n = volume.ReadAt(got, sector, got, sector.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return IsHeader(sector);
        }
    }
}
