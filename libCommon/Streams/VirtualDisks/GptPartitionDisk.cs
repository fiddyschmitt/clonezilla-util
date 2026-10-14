using System;
using System.Buffers.Binary;
using System.Text;

namespace libCommon.Streams.VirtualDisks
{
    /// <summary>
    /// A partition image presented as a whole disk with a GUID partition table: a protective MBR, the table, one Basic
    /// data partition at 1 MiB (sector 2048, the alignment Windows itself uses) holding the image - padded with zeros to
    /// a whole sector - and the backup table in the disk's last sectors. The disk is a whole number of MiB, as Windows
    /// sizes disks (and 7-Zip refuses a VHDX of an odd number of sectors). GPT rather than MBR because an MBR partition
    /// ends at 2 TiB, and a VHDX holds far more. 512-byte sectors, as the volumes this carries expect.
    ///
    /// The disk and partition GUIDs derive from an identity string, so a partition is the same disk on every mount.
    /// Nothing is written anywhere, and reads are positional and stateless.
    /// </summary>
    public sealed class GptPartitionDisk : IPositionalReader
    {
        public const int SectorSize = 512;

        /// <summary>Where the partition starts on the disk.</summary>
        public const long PartitionOffset = 1024 * 1024;

        public const int EntryCount = 128;
        public const int EntrySize = 128;
        const int EntrySectors = EntryCount * EntrySize / SectorSize;

        public static readonly Guid BasicDataPartitionType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

        readonly ComposedReader disk;

        /// <param name="identity">Names the partition (e.g. its container and partition name); the GUIDs derive from it.</param>
        /// <param name="partitionName">The GPT partition name (at most 36 characters are kept).</param>
        public GptPartitionDisk(IPositionalReader image, string identity, string partitionName)
        {
            ImageLength = image.Length;
            DiskGuid = Checksums.DerivedGuid(identity, "gpt disk");
            PartitionGuid = Checksums.DerivedGuid(identity, "gpt partition");

            const long firstLba = PartitionOffset / SectorSize;
            var lastLba = firstLba + PartitionSectors(ImageLength) - 1;
            var totalSectors = LengthFor(ImageLength) / SectorSize;
            Length = totalSectors * SectorSize;

            var entries = new byte[EntryCount * EntrySize];
            BasicDataPartitionType.TryWriteBytes(entries.AsSpan(0, 16));
            PartitionGuid.TryWriteBytes(entries.AsSpan(16, 16));
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(32), firstLba);
            BinaryPrimitives.WriteInt64LittleEndian(entries.AsSpan(40), lastLba);    //inclusive; no attributes
            var name = partitionName.Length > 36 ? partitionName[..36] : partitionName;
            Encoding.Unicode.GetBytes(name).CopyTo(entries.AsSpan(56, 72));
            var entriesCrc = Checksums.Crc32(entries);

            var backupEntriesLba = totalSectors - 1 - EntrySectors;
            var primary = Header(myLba: 1, alternateLba: totalSectors - 1, entriesLba: 2);
            var backup = Header(myLba: totalSectors - 1, alternateLba: 1, entriesLba: backupEntriesLba);

            //only the first entry is anything but zeros
            var firstEntry = entries.AsSpan(0, EntrySize).ToArray();
            disk = new ComposedReader(Length,
            [
                ComposedReader.Piece.Bytes(0, ProtectiveMbr(totalSectors)),
                ComposedReader.Piece.Bytes(SectorSize, primary),
                ComposedReader.Piece.Bytes(2 * SectorSize, firstEntry),
                ComposedReader.Piece.Reader(PartitionOffset, image, ImageLength),
                ComposedReader.Piece.Bytes(backupEntriesLba * SectorSize, firstEntry),
                ComposedReader.Piece.Bytes((totalSectors - 1) * SectorSize, backup),
            ]);

            byte[] Header(long myLba, long alternateLba, long entriesLba)
            {
                var header = new byte[92];
                "EFI PART"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x00010000);          //revision 1.0
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)header.Length);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), myLba);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), alternateLba);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), 2 + EntrySectors);    //first usable LBA
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), totalSectors - 2 - EntrySectors);   //last usable LBA
                DiskGuid.TryWriteBytes(header.AsSpan(56, 16));
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(72), entriesLba);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), EntryCount);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84), EntrySize);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88), entriesCrc);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), Checksums.Crc32(header));   //over the header, its own field zero
                return header;
            }
        }

        /// <summary>The length of the disk for an image of <paramref name="imageLength"/> bytes: 1 MiB, the image padded
        /// to a sector, room for the backup entries and header, rounded up to a whole MiB.</summary>
        public static long LengthFor(long imageLength)
        {
            const long sectorsPerMib = PartitionOffset / SectorSize;
            var sectors = sectorsPerMib + PartitionSectors(imageLength) + EntrySectors + 1;
            return (sectors + sectorsPerMib - 1) / sectorsPerMib * sectorsPerMib * SectorSize;
        }

        static long PartitionSectors(long imageLength) => Math.Max((imageLength + SectorSize - 1) / SectorSize, 1);

        public long ImageLength { get; }
        public Guid DiskGuid { get; }
        public Guid PartitionGuid { get; }
        public long Length { get; }

        public int ReadAt(long position, byte[] buffer, int offset, int count) => disk.ReadAt(position, buffer, offset, count);

        //one partition of type 0xEE covering the disk, so MBR-only tools leave a GPT disk alone
        static byte[] ProtectiveMbr(long totalSectors)
        {
            var sector = new byte[SectorSize];
            var entry = sector.AsSpan(0x1BE, 16);
            entry[2] = 0x02;                                            //CHS of LBA 1: head 0, sector 2, cylinder 0
            entry[4] = 0xEE;
            entry[5] = entry[6] = entry[7] = 0xFF;                      //the end saturates CHS
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)Math.Min(totalSectors - 1, uint.MaxValue));
            sector[0x1FE] = 0x55;
            sector[0x1FF] = 0xAA;
            return sector;
        }
    }
}
