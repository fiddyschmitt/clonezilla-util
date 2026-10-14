using System;
using System.Buffers.Binary;
using System.Text;

namespace libCommon.Streams.VirtualDisks
{
    /// <summary>
    /// A disk in the VHDX format ([MS-VHDX]), laid out as a fixed disk:
    /// <code>
    ///   [0, 1 MiB)     file identifier, two headers, two region tables
    ///   [1, 2 MiB)     the log, empty (the headers' log GUID is zero, so there is nothing to replay)
    ///   [2, 3 MiB)     the metadata region: block size, disk size, disk id, 512-byte sectors
    ///   [3 MiB, ...)   the block allocation table, rounded up to 1 MiB
    ///   payload        the disk, as consecutive fully present 32 MiB blocks
    /// </code>
    /// Little-endian, CRC-32C checksums. The structures are a few hundred bytes among zeros, built once; the BAT is
    /// computed as it is read. 512-byte logical sectors, which is what the volumes this carries expect. At most 64 TiB.
    ///
    /// Give it a disk of whole MiB, as GptPartitionDisk's are: 7-Zip refuses a VHDX whose disk size has the 512 bit set
    /// (an odd number of sectors - its size check tests that bit, where it means the bits below it), although the format
    /// allows any whole number of sectors.
    /// </summary>
    public static class FixedVhdx
    {
        public const long MaxDiskSize = 64L * 1024 * 1024 * 1024 * 1024;
        public const int BlockSize = 32 * 1024 * 1024;
        public const int SectorSize = 512;

        const long Mib = 1024 * 1024;
        const long LogOffset = Mib, LogLength = Mib, MetadataOffset = 2 * Mib, MetadataLength = Mib, BatOffset = 3 * Mib;
        const int HeaderSize = 4 * 1024, RegionTableSize = 64 * 1024, MetadataTableSize = 64 * 1024;

        //payload blocks per sector bitmap block: the BAT holds a (never present) sector bitmap entry after each run of these
        public const long ChunkRatio = (1L << 23) * SectorSize / BlockSize;

        const ulong PayloadBlockFullyPresent = 6;

        static readonly Guid BatRegion = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
        static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
        static readonly Guid FileParametersItem = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");
        static readonly Guid VirtualDiskSizeItem = new("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
        static readonly Guid VirtualDiskIdItem = new("BECA12AB-B2E6-4523-93EF-C309E000C746");
        static readonly Guid LogicalSectorSizeItem = new("8141BF1D-A96F-4709-BA47-F233A8FAAB5F");
        static readonly Guid PhysicalSectorSizeItem = new("CDA348C7-445D-4471-9CC9-E9885251C556");

        public static bool Fits(long diskLength) => diskLength <= MaxDiskSize;

        public static long PayloadBlocks(long diskLength) => (diskLength + BlockSize - 1) / BlockSize;

        /// <summary>BAT entries: one per payload block, and one sector bitmap entry after every <see cref="ChunkRatio"/> of them.</summary>
        public static long BatEntries(long diskLength)
        {
            var blocks = PayloadBlocks(diskLength);
            return blocks + (blocks - 1) / ChunkRatio;
        }

        static long BatLength(long diskLength) => (BatEntries(diskLength) * 8 + Mib - 1) / Mib * Mib;

        /// <summary>Where the disk's bytes start in the file.</summary>
        public static long PayloadOffset(long diskLength) => BatOffset + BatLength(diskLength);

        /// <param name="ids">Derives the file's GUIDs (virtual disk id, file and data write GUIDs), e.g. the partition's identity.</param>
        public static ComposedReader Create(IPositionalReader disk, string ids)
        {
            var diskLength = disk.Length;
            if (diskLength % SectorSize != 0) throw new ArgumentException($"A VHDX disk is whole {SectorSize}-byte sectors; {diskLength:N0} bytes is not.", nameof(disk));
            if (!Fits(diskLength)) throw new ArgumentException($"A disk of {diskLength:N0} bytes does not fit in a VHDX (at most {MaxDiskSize:N0}).", nameof(disk));

            var payloadOffset = PayloadOffset(diskLength);
            var fileWriteGuid = Checksums.DerivedGuid(ids, "vhdx file write");
            var dataWriteGuid = Checksums.DerivedGuid(ids, "vhdx data write");

            return new ComposedReader(payloadOffset + PayloadBlocks(diskLength) * BlockSize,
            [
                ComposedReader.Piece.Bytes(0, FileIdentifier()),
                //two headers; the one with the higher sequence number is current
                ComposedReader.Piece.Bytes(64 * 1024, Header(1, fileWriteGuid, dataWriteGuid)),
                ComposedReader.Piece.Bytes(128 * 1024, Header(2, fileWriteGuid, dataWriteGuid)),
                ComposedReader.Piece.Bytes(192 * 1024, RegionTable(diskLength)),
                ComposedReader.Piece.Bytes(256 * 1024, RegionTable(diskLength)),
                ComposedReader.Piece.Bytes(MetadataOffset, MetadataTable()),
                ComposedReader.Piece.Bytes(MetadataOffset + MetadataTableSize, MetadataItems(diskLength, Checksums.DerivedGuid(ids, "vhdx disk"))),
                ComposedReader.Piece.Generated(BatOffset, BatEntries(diskLength) * 8, (offset, buffer, bufferOffset, count) => ReadBat(payloadOffset, offset, buffer, bufferOffset, count)),
                ComposedReader.Piece.Reader(payloadOffset, disk, diskLength),
            ]);
        }

        static byte[] FileIdentifier()
        {
            var creator = Encoding.Unicode.GetBytes("clonezilla-util");
            var identifier = new byte[8 + creator.Length];
            "vhdxfile"u8.CopyTo(identifier);
            creator.CopyTo(identifier, 8);
            return identifier;
        }

        //the checksum covers the whole 4 KiB, zeros and all; only the fields are served, the rest reads as zeros
        static byte[] Header(ulong sequenceNumber, Guid fileWriteGuid, Guid dataWriteGuid)
        {
            var header = new byte[HeaderSize];
            "head"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), sequenceNumber);
            fileWriteGuid.TryWriteBytes(header.AsSpan(16, 16));
            dataWriteGuid.TryWriteBytes(header.AsSpan(32, 16));
            //48: the log GUID, zero: no log to replay. 64: log version 0
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), 1);              //version
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), (uint)LogLength);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72), LogOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Checksums.Crc32C(header));
            return header.AsSpan(0, 80).ToArray();
        }

        static byte[] RegionTable(long diskLength)
        {
            var table = new byte[RegionTableSize];
            "regi"u8.CopyTo(table);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(8), 2);                //entry count
            Region(table.AsSpan(16, 32), BatRegion, BatOffset, BatLength(diskLength));
            Region(table.AsSpan(48, 32), MetadataRegion, MetadataOffset, MetadataLength);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), Checksums.Crc32C(table));
            return table.AsSpan(0, 80).ToArray();

            static void Region(Span<byte> entry, Guid id, long offset, long length)
            {
                id.TryWriteBytes(entry);
                BinaryPrimitives.WriteInt64LittleEndian(entry[16..], offset);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], (uint)length);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[28..], 1);                //required
            }
        }

        //the items follow the 64 KiB table, packed: file parameters (8), disk size (8), disk id (16), sector sizes (4 + 4)
        static byte[] MetadataTable()
        {
            const uint isVirtualDisk = 2, isRequired = 4;
            var table = new byte[32 + 5 * 32];
            "metadata"u8.CopyTo(table);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(10), 5);              //entry count
            Item(0, FileParametersItem, 0, 8, isRequired);
            Item(1, VirtualDiskSizeItem, 8, 8, isVirtualDisk | isRequired);
            Item(2, VirtualDiskIdItem, 16, 16, isVirtualDisk | isRequired);
            Item(3, LogicalSectorSizeItem, 32, 4, isVirtualDisk | isRequired);
            Item(4, PhysicalSectorSizeItem, 36, 4, isVirtualDisk | isRequired);
            return table;

            void Item(int index, Guid id, int offset, uint length, uint flags)
            {
                var entry = table.AsSpan(32 + index * 32, 32);
                id.TryWriteBytes(entry);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)(MetadataTableSize + offset));   //from the region's start
                BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], length);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], flags);
            }
        }

        static byte[] MetadataItems(long diskLength, Guid virtualDiskId)
        {
            var items = new byte[40];
            BinaryPrimitives.WriteUInt32LittleEndian(items, BlockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(4), 1);               //leave blocks allocated: a fixed disk
            BinaryPrimitives.WriteUInt64LittleEndian(items.AsSpan(8), (ulong)diskLength);
            virtualDiskId.TryWriteBytes(items.AsSpan(16, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(32), SectorSize);      //logical
            BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(36), SectorSize);      //physical
            return items;
        }

        //The BAT, entry by entry: payload block i lives at payloadOffset + i * BlockSize, fully present; the sector bitmap
        //entry closing each chunk (every ChunkRatio + 1 entries) is not present, as on any disk without a parent
        static int ReadBat(long payloadOffset, long offset, byte[] buffer, int bufferOffset, int count)
        {
            Span<byte> entry = stackalloc byte[8];
            var done = 0;
            while (done < count)
            {
                var index = (offset + done) / 8;
                var within = (int)((offset + done) % 8);

                ulong value = 0;
                if ((index + 1) % (ChunkRatio + 1) != 0)
                {
                    var block = index - index / (ChunkRatio + 1);
                    value = (ulong)(payloadOffset + block * BlockSize) | PayloadBlockFullyPresent;
                }
                BinaryPrimitives.WriteUInt64LittleEndian(entry, value);

                var n = Math.Min(8 - within, count - done);
                entry.Slice(within, n).CopyTo(buffer.AsSpan(bufferOffset + done, n));
                done += n;
            }
            return done;
        }
    }
}
