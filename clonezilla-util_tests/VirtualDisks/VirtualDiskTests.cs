using libCommon.Streams;
using libCommon.Streams.VirtualDisks;
using System.Buffers.Binary;
using System.Text;

namespace clonezilla_util_tests.VirtualDisks
{
    // In-process tests of the disks mount-as-image-files serves beside a BitLocker partition's image: the partition on a
    // GPT disk, as a fixed VHD and as a fixed VHDX. The structures are checked against independent readings of the
    // formats (reference CRCs, a minimal VHDX reader that maps the disk through the BAT), not against the code under test.
    [TestClass]
    public class VirtualDiskTests
    {
        const int Mib = 1024 * 1024;

        //not a whole number of sectors, so the padding is exercised
        static byte[] Image(int length = 1_234_567)
        {
            var image = new byte[length];
            new Random(79).NextBytes(image);
            "-FVE-FS-"u8.CopyTo(image.AsSpan(3));
            return image;
        }

        // ----- the GPT disk -----

        [TestMethod]
        public void Gpt_OnePartitionAt1MiB_ThenTheBackupTable()
        {
            var image = Image();
            var disk = new GptPartitionDisk(new PositionalBytes(image), "container\\sda3", "sda3");
            long sectors = (image.Length + 511) / 512, totalSectors = (2048 + sectors + 33 + 2047) / 2048 * 2048;

            Assert.AreEqual(totalSectors * 512, disk.Length);
            Assert.AreEqual(0, disk.Length % Mib, "a whole number of MiB");
            Assert.AreEqual(disk.Length, GptPartitionDisk.LengthFor(image.Length));
            var bytes = ReadAll(disk, chunk: 777);

            //the protective MBR: one 0xEE partition from LBA 1 over the whole disk
            Assert.AreEqual(0x55, bytes[510]);
            Assert.AreEqual(0xAA, bytes[511]);
            Assert.AreEqual(0xEE, bytes[0x1BE + 4]);
            Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x1BE + 8)));
            Assert.AreEqual((uint)(totalSectors - 1), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x1BE + 12)));

            AssertGptHeader(bytes, myLba: 1, alternateLba: totalSectors - 1, entriesLba: 2, totalSectors, disk.DiskGuid);
            AssertGptHeader(bytes, myLba: totalSectors - 1, alternateLba: 1, entriesLba: totalSectors - 33, totalSectors, disk.DiskGuid);

            var entries = bytes.AsSpan(2 * 512, 128 * 128);
            CollectionAssert.AreEqual(entries.ToArray(), bytes.AsSpan((int)(totalSectors - 33) * 512, 128 * 128).ToArray(), "the backup entries");
            var entry = entries[..128];
            Assert.AreEqual(new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"), new Guid(entry[..16]), "Basic data partition");
            Assert.AreEqual(disk.PartitionGuid, new Guid(entry[16..32]));
            Assert.AreEqual(2048L, BinaryPrimitives.ReadInt64LittleEndian(entry[32..]), "first LBA");
            Assert.AreEqual(2048 + sectors - 1, BinaryPrimitives.ReadInt64LittleEndian(entry[40..]), "last LBA (inclusive)");
            Assert.AreEqual(0L, BinaryPrimitives.ReadInt64LittleEndian(entry[48..]), "attributes");
            Assert.AreEqual("sda3", Encoding.Unicode.GetString(entry[56..]).TrimEnd('\0'));
            Assert.IsFalse(entries[128..].ContainsAnyExcept((byte)0), "one entry only");

            Assert.IsFalse(bytes.AsSpan(34 * 512, Mib - 34 * 512).ContainsAnyExcept((byte)0), "zeros up to the partition");
            CollectionAssert.AreEqual(image, bytes.AsSpan(Mib, image.Length).ToArray(), "the image, byte for byte");
            Assert.IsFalse(bytes.AsSpan(Mib + image.Length, (int)(totalSectors - 33) * 512 - Mib - image.Length).ContainsAnyExcept((byte)0), "zeros from the image's end to the backup table");
        }

        static void AssertGptHeader(byte[] disk, long myLba, long alternateLba, long entriesLba, long totalSectors, Guid diskGuid)
        {
            var sector = disk.AsSpan((int)(myLba * 512), 512);
            var header = sector[..92].ToArray();
            Assert.AreEqual("EFI PART", Encoding.ASCII.GetString(header, 0, 8));
            Assert.AreEqual(0x00010000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), "revision");
            Assert.AreEqual(92u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)), "header size");
            var stored = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            header.AsSpan(16, 4).Clear();
            Assert.AreEqual(ReferenceCrc32(header), stored, $"header CRC at LBA {myLba}");
            Assert.AreEqual(myLba, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24)));
            Assert.AreEqual(alternateLba, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32)));
            Assert.AreEqual(34L, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40)), "first usable LBA");
            Assert.AreEqual(totalSectors - 34, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48)), "last usable LBA");
            Assert.AreEqual(diskGuid, new Guid(header.AsSpan(56, 16)));
            Assert.AreEqual(entriesLba, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(72)));
            Assert.AreEqual(128u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)));
            Assert.AreEqual(128u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84)));
            Assert.AreEqual(ReferenceCrc32(disk.AsSpan((int)(entriesLba * 512), 128 * 128)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88)), "entries CRC");
            Assert.IsFalse(sector[92..].ContainsAnyExcept((byte)0));
        }

        // ----- VHD -----

        [TestMethod]
        public void Vhd_IsTheDiskThenAFixedFooter()
        {
            var disk = new GptPartitionDisk(new PositionalBytes(Image()), "container\\sda3", "sda3");
            var vhd = FixedVhd.Create(disk, "container\\sda3");

            Assert.AreEqual(disk.Length + 512, vhd.Length);
            var bytes = ReadAll(vhd, chunk: 1 << 20);
            CollectionAssert.AreEqual(ReadAll(disk, chunk: 1 << 20), bytes[..(int)disk.Length], "the disk, then the footer");
            AssertVhdFooter(bytes.AsSpan(bytes.Length - 512), disk.Length);
        }

        static void AssertVhdFooter(ReadOnlySpan<byte> footer, long diskSize)
        {
            Assert.AreEqual("conectix", Encoding.ASCII.GetString(footer[..8]));
            Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(footer[8..]), "features");
            Assert.AreEqual(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(footer[12..]), "format version");
            Assert.AreEqual(ulong.MaxValue, BinaryPrimitives.ReadUInt64BigEndian(footer[16..]), "data offset (fixed disk)");
            Assert.AreEqual("win ", Encoding.ASCII.GetString(footer.Slice(28, 4)), "creator application");
            Assert.AreEqual((ulong)diskSize, BinaryPrimitives.ReadUInt64BigEndian(footer[40..]), "original size");
            Assert.AreEqual((ulong)diskSize, BinaryPrimitives.ReadUInt64BigEndian(footer[48..]), "current size");
            var (cylinders, heads, sectorsPerTrack) = FixedVhd.Geometry(diskSize / 512);
            Assert.AreEqual(cylinders, BinaryPrimitives.ReadUInt16BigEndian(footer[56..]), "cylinders");
            Assert.AreEqual(heads, footer[58], "heads");
            Assert.AreEqual(sectorsPerTrack, footer[59], "sectors per track");
            Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32BigEndian(footer[60..]), "disk type: fixed");

            var copy = footer.ToArray();
            var stored = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(64));
            copy.AsSpan(64, 4).Clear();
            uint sum = 0;
            foreach (var b in copy) sum += b;
            Assert.AreEqual(~sum, stored, "checksum");
        }

        [TestMethod]
        public void Vhd_HoldsAtMost2040GiB()
        {
            const long max = 2040L * 1024 * 1024 * 1024;
            Assert.IsTrue(FixedVhd.Fits(max));
            Assert.IsFalse(FixedVhd.Fits(max + 512));

            //1 MiB before the partition, and the backup table rounds up to another
            var largest = new GptPartitionDisk(new LengthOnly(max - 2 * Mib), "x", "x");
            Assert.AreEqual(max, largest.Length);
            var footer = ReadRange(FixedVhd.Create(largest, "x"), max, 512);
            AssertVhdFooter(footer, max);

            var tooLarge = new GptPartitionDisk(new LengthOnly(max - Mib), "x", "x");
            Assert.AreEqual(max + Mib, tooLarge.Length);
            Assert.ThrowsExactly<ArgumentException>(() => FixedVhd.Create(tooLarge, "x"));
        }

        //worked through the VHD specification's algorithm by hand
        [TestMethod]
        [DataRow(20480L, 301, 4, 17)]                 //10 MiB
        [DataRow(2097152L, 2080, 16, 63)]             //1 GiB
        [DataRow(117442560L, 28784, 16, 255)]         //56 GiB + 1 MiB: past the 63-sector range
        [DataRow(4278190080L, 65535, 16, 255)]        //2040 GiB: the maximum geometry
        public void Vhd_Geometry_FollowsTheSpecification(long sectors, int cylinders, int heads, int sectorsPerTrack)
        {
            Assert.AreEqual(((ushort)cylinders, (byte)heads, (byte)sectorsPerTrack), FixedVhd.Geometry(sectors));
        }

        // ----- VHDX -----

        [TestMethod]
        public void Vhdx_IsAValidFixedDisk_ThatReadsBackAsTheDisk()
        {
            var disk = new GptPartitionDisk(new PositionalBytes(Image(3 * Mib + 100)), "container\\sda3", "sda3");
            var vhdx = FixedVhdx.Create(disk, "container\\sda3");
            var parsed = ParsedVhdx.Parse(vhdx);

            Assert.AreEqual(32 * Mib, parsed.BlockSize);
            Assert.AreEqual(disk.Length, parsed.DiskSize);
            Assert.AreEqual(512, parsed.LogicalSectorSize);
            Assert.AreEqual(512, parsed.PhysicalSectorSize);
            Assert.IsTrue(parsed.LeaveBlocksAllocated, "a fixed disk");
            Assert.AreEqual(1, parsed.Bat.Length, "one payload block, no chunk boundary");
            Assert.AreEqual(parsed.PayloadEnd, vhdx.Length, "the file ends with the last payload block");
            CollectionAssert.AreEqual(ReadAll(disk, chunk: 1 << 20), parsed.ReadDisk(0, (int)disk.Length), "the disk, read through the BAT");
        }

        [TestMethod]
        public void Vhdx_BatHasASectorBitmapEntryAfterEveryChunk()
        {
            //5 GiB is 161 payload blocks: past one chunk of 128
            var disk = new GptPartitionDisk(new PatternSource(5L * 1024 * Mib), "x", "big");
            var vhdx = FixedVhdx.Create(disk, "x");
            var parsed = ParsedVhdx.Parse(vhdx);

            var blocks = FixedVhdx.PayloadBlocks(disk.Length);
            Assert.AreEqual(161L, blocks);
            Assert.AreEqual(blocks + 1, parsed.Bat.Length);
            Assert.AreEqual(0ul, parsed.Bat[128], "the first chunk's sector bitmap: not present");

            long block = 32L * Mib;
            foreach (var position in new[] { 0L, Mib + 12_345, 128 * block - 100, 128 * block + 7, 160 * block + 3, disk.Length - 1000 })
            {
                var count = (int)Math.Min(4096, disk.Length - position);
                CollectionAssert.AreEqual(ReadRange(disk, position, count), parsed.ReadDisk(position, count), $"the disk at {position:N0}");
            }
        }

        //7-Zip refuses a VHDX whose disk size has the 512 bit set (its size check tests that bit, where it means the bits
        //below it), so whatever the image's length, the disk is a whole MiB
        [TestMethod]
        [DataRow(1L)]
        [DataRow(511L)]
        [DataRow(513L)]
        [DataRow(1_048_575L)]
        [DataRow(1_049_088L)]                 //1 MiB and a sector
        [DataRow(1_234_567L)]
        [DataRow(60_129_542_144L)]            //the BitLocker test image's sda3: as a bare disk, an odd 117,442,593 sectors
        public void Vhdx_DiskSizeNeverHasThe512BitSet(long imageLength)
        {
            var parsed = ParsedVhdx.Parse(FixedVhdx.Create(new GptPartitionDisk(new LengthOnly(imageLength), "x", "x"), "x"));

            Assert.AreEqual(0L, parsed.DiskSize % Mib, "a whole number of MiB");
            Assert.AreEqual(0L, parsed.DiskSize & 512, "7-Zip's test: the 512 bit of the disk size");
        }

        [TestMethod]
        public void Vhdx_HoldsAtMost64TiB()
        {
            const long max = 64L * 1024 * 1024 * 1024 * 1024;
            Assert.IsTrue(FixedVhdx.Fits(max));
            Assert.IsFalse(FixedVhdx.Fits(max + 512));
            Assert.ThrowsExactly<ArgumentException>(() => FixedVhdx.Create(new GptPartitionDisk(new LengthOnly(max), "x", "x"), "x"));
        }

        // ----- identity -----

        //a partition is the same disk on every mount; two partitions attached together must not collide
        [TestMethod]
        public void Identity_FixesEveryId()
        {
            var image = Image();
            byte[] Vhdx(string identity) => ReadRange(FixedVhdx.Create(new GptPartitionDisk(new PositionalBytes(image), identity, "sda3"), identity), 0, 8 * Mib);
            byte[] Vhd(string identity) => ReadAll(FixedVhd.Create(new GptPartitionDisk(new PositionalBytes(image), identity, "sda3"), identity), chunk: 1 << 20);

            CollectionAssert.AreEqual(Vhdx("a\\sda3"), Vhdx("a\\sda3"));
            CollectionAssert.AreEqual(Vhd("a\\sda3"), Vhd("a\\sda3"));

            var a = new GptPartitionDisk(new PositionalBytes(image), "a\\sda3", "sda3");
            var b = new GptPartitionDisk(new PositionalBytes(image), "b\\sda3", "sda3");
            Assert.AreNotEqual(a.DiskGuid, b.DiskGuid);
            Assert.AreNotEqual(a.PartitionGuid, b.PartitionGuid);
            Assert.AreNotEqual(a.DiskGuid, a.PartitionGuid);
            Assert.AreNotEqual(ParsedVhdx.Parse(FixedVhdx.Create(a, "a\\sda3")).DiskId, ParsedVhdx.Parse(FixedVhdx.Create(b, "b\\sda3")).DiskId);
            var vhdA = Vhd("a\\sda3");
            var vhdB = Vhd("b\\sda3");
            Assert.IsFalse(vhdA.AsSpan(vhdA.Length - 512 + 68, 16).SequenceEqual(vhdB.AsSpan(vhdB.Length - 512 + 68, 16)), "VHD unique id");
        }

        // ----- recognising BitLocker -----

        [TestMethod]
        [DataRow("bitlocker", true)]
        [DataRow("to-go", true)]
        [DataRow("mswin-without-identifier", false)]
        [DataRow("ntfs", false)]
        [DataRow("zeros", false)]
        [DataRow("short", false)]
        public void BitLocker_IsRecognisedByItsFirstSector(string kind, bool expected)
        {
            var sector = new byte[kind == "short" ? 100 : 4096];
            switch (kind)
            {
                case "bitlocker": case "short": "-FVE-FS-"u8.CopyTo(sector.AsSpan(3)); break;
                case "to-go":
                    "MSWIN4.1"u8.CopyTo(sector.AsSpan(3));
                    new Guid("4967D63B-2E29-4AD8-8399-F6A339E3D001").TryWriteBytes(sector.AsSpan(424, 16));
                    break;
                case "mswin-without-identifier": "MSWIN4.1"u8.CopyTo(sector.AsSpan(3)); break;
                case "ntfs": "NTFS    "u8.CopyTo(sector.AsSpan(3)); break;
            }

            Assert.AreEqual(expected, BitLockerVolume.IsVolume(new PositionalBytes(sector)));
        }

        // ----- the pieces -----

        [TestMethod]
        public void Checksums_MatchTheStandardCheckValues()
        {
            Assert.AreEqual(0xCBF43926u, Checksums.Crc32("123456789"u8));
            Assert.AreEqual(0xE3069283u, Checksums.Crc32C("123456789"u8));
            Assert.AreEqual(0xCBF43926u, ReferenceCrc32("123456789"u8));
            Assert.AreEqual(0xE3069283u, ReferenceCrc32C("123456789"u8));
        }

        [TestMethod]
        public void ComposedReader_ServesPiecesAndZerosBetween()
        {
            var source = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
            var composed = new ComposedReader(300,
            [
                ComposedReader.Piece.Bytes(10, [1, 2, 3]),
                ComposedReader.Piece.Reader(100, new PositionalBytes(source), 50),
                ComposedReader.Piece.Generated(200, 8, (offset, buffer, bufferOffset, count) => { buffer.AsSpan(bufferOffset, count).Fill(0xAB); return count; }),
            ]);

            var expected = new byte[300];
            new byte[] { 1, 2, 3 }.CopyTo(expected, 10);
            Array.Copy(source, 0, expected, 100, 50);
            expected.AsSpan(200, 8).Fill(0xAB);
            CollectionAssert.AreEqual(expected, ReadAll(composed, chunk: 7));

            Assert.AreEqual(10, composed.ReadAt(0, new byte[300], 0, 300), "a gap is served up to the next piece");
            Assert.AreEqual(3, composed.ReadAt(10, new byte[300], 0, 300), "a piece is served up to its end");
            Assert.AreEqual(0, composed.ReadAt(300, new byte[10], 0, 10), "the end");
            Assert.ThrowsExactly<ArgumentException>(() => new ComposedReader(300, [ComposedReader.Piece.Bytes(10, new byte[5]), ComposedReader.Piece.Bytes(14, new byte[5])]));
            Assert.ThrowsExactly<ArgumentException>(() => new ComposedReader(300, [ComposedReader.Piece.Bytes(298, new byte[5])]));
        }

        //positional and stateless: concurrent readers all see the same disk
        [TestMethod]
        public void ConcurrentReads_ServeTheSameBytes()
        {
            var disk = new GptPartitionDisk(new PositionalBytes(Image(3 * Mib + 100)), "x", "x");
            var vhdx = FixedVhdx.Create(disk, "x");
            var expected = ReadAll(vhdx, chunk: 1 << 20);

            Parallel.For(0, 2000, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
            {
                var random = new Random(i);
                var position = random.NextInt64(vhdx.Length);
                var buffer = new byte[random.Next(1, 70_000)];
                var n = vhdx.ReadAt(position, buffer, 0, buffer.Length);
                Assert.IsTrue(n > 0);
                CollectionAssert.AreEqual(expected.AsSpan((int)position, n).ToArray(), buffer.AsSpan(0, n).ToArray(), $"read at {position}");
            });
        }

        // ----- helpers -----

        static byte[] ReadAll(IPositionalReader source, int chunk)
        {
            var result = new byte[source.Length];
            var total = 0;
            while (total < result.Length)
            {
                var n = source.ReadAt(total, result, total, Math.Min(chunk, result.Length - total));
                if (n <= 0) break;
                total += n;
            }
            Assert.AreEqual(result.Length, total, "the whole source reads");
            return result;
        }

        static byte[] ReadRange(IPositionalReader source, long position, int count)
        {
            var result = new byte[count];
            var total = 0;
            while (total < count)
            {
                var n = source.ReadAt(position + total, result, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            Assert.AreEqual(count, total, $"{count} bytes at {position:N0}");
            return result;
        }

        //bit by bit, independent of the table and the intrinsic the product uses
        static uint ReferenceCrc32(ReadOnlySpan<byte> data) => ReferenceCrc(data, 0xEDB88320);
        static uint ReferenceCrc32C(ReadOnlySpan<byte> data) => ReferenceCrc(data, 0x82F63B78);

        static uint ReferenceCrc(ReadOnlySpan<byte> data, uint polynomial)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            }
            return ~crc;
        }

        /// <summary>
        /// A minimal VHDX reader written from [MS-VHDX]: validates the identifier, both headers, both region tables and
        /// the metadata, then reads the virtual disk through the BAT.
        /// </summary>
        sealed class ParsedVhdx
        {
            static readonly Guid BatRegion = new("2DC27766-F623-4200-9D64-115E9BFD4A08");
            static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");

            IPositionalReader file = null!;
            long chunkRatio;
            public int BlockSize, LogicalSectorSize, PhysicalSectorSize;
            public long DiskSize, PayloadEnd;
            public bool LeaveBlocksAllocated;
            public Guid DiskId;
            public ulong[] Bat = [];

            public static ParsedVhdx Parse(IPositionalReader file)
            {
                var parsed = new ParsedVhdx { file = file };
                Assert.AreEqual("vhdxfile", Encoding.ASCII.GetString(ReadRange(file, 0, 8)));

                //both headers valid; the second is current (higher sequence number)
                var sequences = new List<ulong>();
                foreach (var offset in new[] { 64 * 1024L, 128 * 1024L })
                {
                    var header = ReadRange(file, offset, 4096);
                    Assert.AreEqual("head", Encoding.ASCII.GetString(header, 0, 4));
                    AssertCrc32C(header, 4, $"header at {offset}");
                    sequences.Add(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)));
                    Assert.IsFalse(header.AsSpan(48, 16).ContainsAnyExcept((byte)0), "log GUID zero: nothing to replay");
                    Assert.AreEqual((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(64)), "log version");
                    Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(66)), "version");
                    Assert.AreEqual((uint)Mib, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68)), "log length");
                    Assert.AreEqual((ulong)Mib, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72)), "log offset");
                }
                CollectionAssert.AreEqual(new ulong[] { 1, 2 }, sequences);

                //both region tables valid and alike
                var table = ReadRange(file, 192 * 1024, 64 * 1024);
                CollectionAssert.AreEqual(table, ReadRange(file, 256 * 1024, 64 * 1024), "the region tables");
                Assert.AreEqual("regi", Encoding.ASCII.GetString(table, 0, 4));
                AssertCrc32C(table, 4, "region table");
                Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(8)));
                var regions = Enumerable.Range(0, 2).ToDictionary(
                    i => new Guid(table.AsSpan(16 + i * 32, 16)),
                    i => (Offset: BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(32 + i * 32)),
                          Length: (long)BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(40 + i * 32)),
                          Required: BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(44 + i * 32))));
                foreach (var region in regions.Values)
                {
                    Assert.AreEqual(1u, region.Required);
                    Assert.AreEqual(0, region.Offset % Mib);
                    Assert.AreEqual(0, region.Length % Mib);
                    Assert.IsTrue(region.Offset >= 2 * Mib, "after the header section and the log");
                }

                //metadata: five items, every one required
                var metadata = regions[MetadataRegion];
                var metadataTable = ReadRange(file, metadata.Offset, 64 * 1024);
                Assert.AreEqual("metadata", Encoding.ASCII.GetString(metadataTable, 0, 8));
                var count = BinaryPrimitives.ReadUInt16LittleEndian(metadataTable.AsSpan(10));
                Assert.AreEqual((ushort)5, count);
                for (var i = 0; i < count; i++)
                {
                    var entry = metadataTable.AsSpan(32 + i * 32, 32);
                    var id = new Guid(entry[..16]).ToString().ToUpperInvariant();
                    var itemOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
                    var itemLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
                    Assert.IsTrue(itemOffset >= 64 * 1024, "items follow the table");
                    var item = ReadRange(file, metadata.Offset + itemOffset, itemLength);

                    switch (id)
                    {
                        case "CAA16737-FA36-4D43-B3B6-33F0AA44E76B":
                            Assert.AreEqual(4u, flags, "file parameters: required, not virtual disk metadata");
                            parsed.BlockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(item);
                            var parameterFlags = BinaryPrimitives.ReadUInt32LittleEndian(item.AsSpan(4));
                            parsed.LeaveBlocksAllocated = (parameterFlags & 1) != 0;
                            Assert.AreEqual(0u, parameterFlags & 2, "no parent");
                            break;
                        case "2FA54224-CD1B-4876-B211-5DBED83BF4B8":
                            Assert.AreEqual(6u, flags);
                            parsed.DiskSize = BinaryPrimitives.ReadInt64LittleEndian(item);
                            break;
                        case "BECA12AB-B2E6-4523-93EF-C309E000C746":
                            Assert.AreEqual(6u, flags);
                            parsed.DiskId = new Guid(item);
                            break;
                        case "8141BF1D-A96F-4709-BA47-F233A8FAAB5F":
                            Assert.AreEqual(6u, flags);
                            parsed.LogicalSectorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(item);
                            break;
                        case "CDA348C7-445D-4471-9CC9-E9885251C556":
                            Assert.AreEqual(6u, flags);
                            parsed.PhysicalSectorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(item);
                            break;
                        default:
                            Assert.Fail($"unexpected metadata item {id}");
                            break;
                    }
                }
                Assert.AreEqual(0, parsed.DiskSize % parsed.LogicalSectorSize);

                //the BAT: every payload block fully present, 1 MiB aligned, past the regions; sector bitmaps not present
                parsed.chunkRatio = (1L << 23) * parsed.LogicalSectorSize / parsed.BlockSize;
                var blocks = (parsed.DiskSize + parsed.BlockSize - 1) / parsed.BlockSize;
                var entries = blocks + (blocks - 1) / parsed.chunkRatio;
                var bat = regions[BatRegion];
                Assert.IsTrue(entries * 8 <= bat.Length);
                var batBytes = ReadRange(file, bat.Offset, (int)(entries * 8));
                parsed.Bat = Enumerable.Range(0, (int)entries).Select(i => BinaryPrimitives.ReadUInt64LittleEndian(batBytes.AsSpan(i * 8))).ToArray();
                for (var i = 0; i < parsed.Bat.Length; i++)
                {
                    var value = parsed.Bat[i];
                    if ((i + 1) % (parsed.chunkRatio + 1) == 0)
                    {
                        Assert.AreEqual(0ul, value, $"sector bitmap entry {i}");
                        continue;
                    }
                    Assert.AreEqual(6ul, value & 7, $"BAT entry {i}: PAYLOAD_BLOCK_FULLY_PRESENT");
                    var fileOffset = (long)(value & ~0xFFFFFul);
                    Assert.IsTrue(fileOffset >= bat.Offset + bat.Length, $"block {i} lies past the BAT");
                    parsed.PayloadEnd = Math.Max(parsed.PayloadEnd, fileOffset + parsed.BlockSize);
                }
                Assert.IsTrue(parsed.PayloadEnd <= file.Length, "every block lies within the file");
                return parsed;
            }

            public byte[] ReadDisk(long position, int count)
            {
                var result = new byte[count];
                for (var done = 0; done < count;)
                {
                    var block = (position + done) / BlockSize;
                    var within = (position + done) % BlockSize;
                    var entry = Bat[block + block / chunkRatio];
                    var n = (int)Math.Min(count - done, BlockSize - within);
                    ReadRange(file, (long)(entry & ~0xFFFFFul) + within, n).CopyTo(result, done);
                    done += n;
                }
                return result;
            }

            static void AssertCrc32C(byte[] bytes, int checksumOffset, string what)
            {
                var copy = (byte[])bytes.Clone();
                var stored = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(checksumOffset));
                copy.AsSpan(checksumOffset, 4).Clear();
                Assert.AreEqual(ReferenceCrc32C(copy), stored, $"{what} CRC-32C");
            }
        }

        /// <summary>A byte array read positionally, at most 1000 bytes per call so callers' loops are exercised.</summary>
        sealed class PositionalBytes(byte[] bytes) : IPositionalReader
        {
            public int ReadAt(long position, byte[] buffer, int offset, int count)
            {
                if (position >= bytes.Length) return 0;
                var n = (int)Math.Min(Math.Min(count, 1000), bytes.Length - position);
                Array.Copy(bytes, position, buffer, offset, n);
                return n;
            }

            public long Length => bytes.Length;
        }

        /// <summary>A large source whose every byte is a function of its position, for the multi-chunk BAT.</summary>
        sealed class PatternSource(long length) : IPositionalReader
        {
            public int ReadAt(long position, byte[] buffer, int offset, int count)
            {
                var n = (int)Math.Min(count, length - position);
                for (var i = 0; i < n; i++)
                {
                    var p = position + i;
                    buffer[offset + i] = (byte)(p ^ (p >> 9) ^ (p >> 21) ^ (p >> 33));
                }
                return Math.Max(n, 0);
            }

            public long Length => length;
        }

        /// <summary>A source with a length and no bytes worth reading: for the size limits.</summary>
        sealed class LengthOnly(long length) : IPositionalReader
        {
            public int ReadAt(long position, byte[] buffer, int offset, int count) => 0;
            public long Length => length;
        }
    }
}
