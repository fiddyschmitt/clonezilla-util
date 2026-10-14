using System;
using System.Buffers.Binary;

namespace libCommon.Streams.VirtualDisks
{
    /// <summary>
    /// A disk in the fixed VHD format (Virtual Hard Disk Image Format Specification): the disk's bytes, then a 512-byte
    /// big-endian footer. Windows attaches one of at most 2040 GiB.
    ///
    /// Not served at present: mount-as-image-files serves a BitLocker partition as a .vhdx only (FixedVhdx). Kept, and
    /// tested, so a .vhd can be served again beside it (see MountedPartitionImage.CreateVhdxFileEntry).
    /// </summary>
    public static class FixedVhd
    {
        /// <summary>The largest disk (excluding the footer) a VHD may describe: Windows refuses to attach anything larger.</summary>
        public const long MaxDiskSize = 2040L * 1024 * 1024 * 1024;

        public const int FooterSize = 512;

        public static bool Fits(long diskLength) => diskLength <= MaxDiskSize;

        /// <param name="ids">Derives the VHD's unique id, e.g. the partition's identity.</param>
        public static ComposedReader Create(IPositionalReader disk, string ids)
        {
            if (disk.Length % GptPartitionDisk.SectorSize != 0) throw new ArgumentException($"A VHD disk is whole sectors; {disk.Length:N0} bytes is not.", nameof(disk));
            if (!Fits(disk.Length)) throw new ArgumentException($"A disk of {disk.Length:N0} bytes does not fit in a VHD (at most {MaxDiskSize:N0}).", nameof(disk));

            return new ComposedReader(disk.Length + FooterSize,
            [
                ComposedReader.Piece.Reader(0, disk, disk.Length),
                ComposedReader.Piece.Bytes(disk.Length, Footer(disk.Length, Checksums.DerivedGuid(ids, "vhd"))),
            ]);
        }

        static byte[] Footer(long diskSize, Guid uniqueId)
        {
            var f = new byte[FooterSize];
            "conectix"u8.CopyTo(f);
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 0x00000002);              //features: the reserved bit, always set
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);             //format version 1.0
            BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(16), ulong.MaxValue);          //data offset: none (fixed disk)
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), 0);                       //timestamp (2000-01-01): fixed, so the bytes are too
            //Creator "win ": readers such as qemu then size the disk by the footer's current size, as Windows does,
            //rather than by the CHS geometry, which rounds the size down
            "win "u8.CopyTo(f.AsSpan(28));
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(32), 0x000A0000);             //creator version
            "Wi2k"u8.CopyTo(f.AsSpan(36));                                                //creator host OS: Windows
            BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(40), (ulong)diskSize);         //original size
            BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(48), (ulong)diskSize);         //current size
            var (cylinders, heads, sectorsPerTrack) = Geometry(diskSize / GptPartitionDisk.SectorSize);
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(56), cylinders);
            f[58] = heads;
            f[59] = sectorsPerTrack;
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), 2);                       //disk type: fixed
            uniqueId.TryWriteBytes(f.AsSpan(68, 16));
            f[84] = 0;                                                                    //saved state: none

            uint sum = 0;
            foreach (var b in f) sum += b;
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), ~sum);                    //checksum: one's complement of the byte sum
            return f;
        }

        /// <summary>The CHS geometry the VHD specification derives from a disk's sector count (Appendix: CHS Calculation).</summary>
        public static (ushort Cylinders, byte Heads, byte SectorsPerTrack) Geometry(long totalSectors)
        {
            totalSectors = Math.Min(totalSectors, 65535L * 16 * 255);

            long sectorsPerTrack, heads, cylinderTimesHeads;
            if (totalSectors >= 65535L * 16 * 63)
            {
                sectorsPerTrack = 255;
                heads = 16;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
            }
            else
            {
                sectorsPerTrack = 17;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
                heads = Math.Max((cylinderTimesHeads + 1023) / 1024, 4);
                if (cylinderTimesHeads >= heads * 1024 || heads > 16)
                {
                    sectorsPerTrack = 31;
                    heads = 16;
                    cylinderTimesHeads = totalSectors / sectorsPerTrack;
                }
                if (cylinderTimesHeads >= heads * 1024)
                {
                    sectorsPerTrack = 63;
                    heads = 16;
                    cylinderTimesHeads = totalSectors / sectorsPerTrack;
                }
            }
            return ((ushort)(cylinderTimesHeads / heads), (byte)heads, (byte)sectorsPerTrack);
        }
    }
}
