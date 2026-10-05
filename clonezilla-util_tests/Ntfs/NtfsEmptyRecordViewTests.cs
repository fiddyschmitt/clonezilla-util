using libCommon.Streams;
using System.Buffers.Binary;

namespace clonezilla_util_tests.Ntfs
{
    // In-process tests of the view that presents stamped-but-empty MFT records as empty (gh #79), over a synthetic
    // NTFS volume: a boot sector, an MFT in two runs, and records of every kind the rule must and must not touch.
    [TestClass]
    public class NtfsEmptyRecordViewTests
    {
        //the records the view must present as empty: "patched" means bytes 0-1 of the record read as zero
        static readonly int[] StampedRecords = [5, 6, 10];

        static byte[] BuildVolume() => SyntheticNtfs.Build(new Dictionary<int, byte[]>
        {
            [1] = SyntheticNtfs.FileRecord(usn: 9),          //a live record: never touched, whatever its USN
            [3] = SyntheticNtfs.Signature("BAAD"),           //7-Zip knows this signature; not ours to touch
            [5] = SyntheticNtfs.ZeroedStamped(7),            //the gh #79 record
            [6] = SyntheticNtfs.ZeroedStamped(0x1234),       //a big USN: bytes 34 12 00 00
            [8] = SyntheticNtfs.ZeroedStamped(NtfsEmptyRecordView.SevenZipAcceptsUpTo),       //7-Zip accepts this one: untouched
            [9] = SyntheticNtfs.ZeroedStamped(3),                                              //likewise
            [10] = SyntheticNtfs.ZeroedStamped(NtfsEmptyRecordView.SevenZipAcceptsUpTo + 1),  //the first value it rejects, in the second run
            [11] = SyntheticNtfs.ZeroedStamped(7),           //a slot past the MFT's size: outside the MFT, untouched
        });

        static byte[] Expected(byte[] source)
        {
            var expected = (byte[])source.Clone();
            foreach (var record in StampedRecords)
            {
                var offset = SyntheticNtfs.RecordOffset(record);
                expected[offset] = 0;
                expected[offset + 1] = 0;
            }
            return expected;
        }

        static NtfsEmptyRecordView View(byte[] image, bool positional) =>
            new(positional ? new PositionalBytes(image) : new MemoryStream(image, writable: false), "[test]");

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public void WholeRead_PatchesOnlyStampedRecordsInsideTheMft(bool positionalSource)
        {
            var source = BuildVolume();
            var untouched = (byte[])source.Clone();
            var view = View(source, positionalSource);

            var served = new byte[source.Length];
            Assert.AreEqual(served.Length, ReadAll(view, 0, served));

            Assert.IsTrue(view.IsNtfs);
            CollectionAssert.AreEqual(Expected(source), served, "served bytes");
            CollectionAssert.AreEquivalent(StampedRecords.Select(r => (long)r).ToArray(), view.PatchedRecords.ToArray(), "patched record numbers");
            CollectionAssert.AreEqual(untouched, source, "the source must never be modified");
            Assert.AreEqual(0x07, source[SyntheticNtfs.Decoy], "the decoy outside the MFT keeps its 07");
            Assert.AreEqual(0x07, served[SyntheticNtfs.Decoy], "the decoy outside the MFT is served as is");
        }

        [TestMethod]
        public void PartialReads_AroundARecordHead()
        {
            var source = BuildVolume();
            var head = SyntheticNtfs.RecordOffset(5);

            //(read start relative to the head, length, which of the head's two bytes the read holds)
            (int Start, int Length, string Holds)[] cases =
            [
                (-100, 101, "byte 0 only"),
                (1, 3, "byte 1 only"),
                (-4, 6, "bytes 0-1 but not 2-3"),
                (-7, 20, "the whole head"),
                (2, 10, "neither"),
            ];
            foreach (var (start, length, holds) in cases)
            {
                var view = View(source, positional: true);   //fresh: PatchedRecords must reflect this read alone
                var buffer = new byte[length];
                Assert.AreEqual(length, ReadAll(view, head + start, buffer), holds);

                for (var i = 0; i < length; i++)
                {
                    var disk = head + start + i;
                    var expected = disk == head || disk == head + 1 ? (byte)0 : source[disk];
                    Assert.AreEqual(expected, buffer[i], $"{holds}: byte at {disk - head} relative to the head");
                }
                var shouldPatch = holds != "neither";
                Assert.AreEqual(shouldPatch, view.PatchedRecords.Contains(5), $"{holds}: record 5 reported as patched");
            }
        }

        [TestMethod]
        public void ChunkedStreamReads_MatchOneRead()
        {
            var source = BuildVolume();
            var view = View(source, positional: false);

            var served = new byte[source.Length];
            view.Seek(0, SeekOrigin.Begin);
            var total = 0;
            while (total < served.Length)
            {
                //an odd chunk size, so record heads fall at every position within a read, and across two reads
                var n = view.Read(served, total, Math.Min(700, served.Length - total));
                Assert.IsTrue(n > 0);
                total += n;
            }

            CollectionAssert.AreEqual(Expected(source), served);
            Assert.AreEqual(served.Length, view.Position);
        }

        [TestMethod]
        public void NotNtfs_IsAPassThrough()
        {
            var source = BuildVolume();
            source[3] = (byte)'X';   //no longer "NTFS    "
            var view = View(source, positional: true);

            var served = new byte[source.Length];
            ReadAll(view, 0, served);

            Assert.IsFalse(view.IsNtfs);
            CollectionAssert.AreEqual(source, served);
            Assert.AreEqual(0, view.PatchedRecords.Count);
        }

        [TestMethod]
        public void UnreadableMft_IsAPassThrough()
        {
            var source = BuildVolume();
            source[SyntheticNtfs.RecordOffset(0)] = (byte)'X';   //record 0 no longer says FILE
            var view = View(source, positional: true);

            var served = new byte[source.Length];
            ReadAll(view, 0, served);

            Assert.IsFalse(view.IsNtfs);
            CollectionAssert.AreEqual(source, served);
        }

        static int ReadAll(NtfsEmptyRecordView view, long position, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var n = view.ReadAt(position + total, buffer, total, buffer.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>A byte array read positionally (the compressed serving path's shape), returning at most 1000 bytes
        /// per call so callers' loops are exercised.</summary>
        sealed class PositionalBytes(byte[] bytes) : Stream, IPositionalReader
        {
            public int ReadAt(long position, byte[] buffer, int offset, int count)
            {
                if (position >= bytes.Length) return 0;
                var n = (int)Math.Min(Math.Min(count, 1000), bytes.Length - position);
                Array.Copy(bytes, position, buffer, offset, n);
                return n;
            }

            public override long Length => bytes.Length;
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Position { get; set; }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("the view must read positionally");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// A small NTFS volume, enough of one for the view: a boot sector; MFT record 0 whose unnamed $DATA runs in two
    /// pieces (clusters 2-3, then cluster 10, for 11 records - the last slot of the second run is past the MFT's
    /// size); a decoy "07 00 00 00" outside the MFT. Records are stamped the way the driver stamps them.
    /// </summary>
    static class SyntheticNtfs
    {
        public const int BytesPerSector = 512;
        public const int ClusterSize = 4096;
        public const int RecordSize = 1024;
        public const int RecordCount = 11;
        public const long RunA = 2 * ClusterSize;
        public const long RunB = 10 * ClusterSize;
        public const long Decoy = 20 * ClusterSize;

        public static long RecordOffset(int record) => record < 8 ? RunA + record * RecordSize : RunB + (record - 8) * RecordSize;

        public static byte[] Build(Dictionary<int, byte[]> records)
        {
            var image = new byte[24 * ClusterSize];

            "NTFS    "u8.CopyTo(image.AsSpan(3));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x0B), BytesPerSector);
            image[0x0D] = ClusterSize / BytesPerSector;
            BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(0x30), RunA / ClusterSize);   //MFT cluster
            image[0x40] = unchecked((byte)-10);                                                //records of 2^10 bytes

            //record 0: FILE, update sequence array at 0x30 (USN + one entry per sector), one attribute at 0x38
            var record0 = new byte[RecordSize];
            "FILE"u8.CopyTo(record0);
            BinaryPrimitives.WriteUInt16LittleEndian(record0.AsSpan(4), 0x30);
            BinaryPrimitives.WriteUInt16LittleEndian(record0.AsSpan(6), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(record0.AsSpan(0x14), 0x38);
            BinaryPrimitives.WriteUInt32LittleEndian(record0.AsSpan(0x1C), RecordSize);

            //the unnamed, non-resident $DATA: run list at +0x40, sizes at +0x28..
            const int a = 0x38;
            BinaryPrimitives.WriteUInt32LittleEndian(record0.AsSpan(a), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(record0.AsSpan(a + 4), 0x48);
            record0[a + 8] = 1;   //non-resident
            record0[a + 9] = 0;   //no name
            BinaryPrimitives.WriteUInt16LittleEndian(record0.AsSpan(a + 0x20), 0x40);
            BinaryPrimitives.WriteInt64LittleEndian(record0.AsSpan(a + 0x28), 3 * ClusterSize);
            BinaryPrimitives.WriteInt64LittleEndian(record0.AsSpan(a + 0x30), RecordCount * RecordSize);
            BinaryPrimitives.WriteInt64LittleEndian(record0.AsSpan(a + 0x38), RecordCount * RecordSize);
            //runs: 2 clusters at cluster +2, then 1 cluster at +8 from there (cluster 10); terminator
            record0[a + 0x40] = 0x11; record0[a + 0x41] = 2; record0[a + 0x42] = 2;
            record0[a + 0x43] = 0x11; record0[a + 0x44] = 1; record0[a + 0x45] = 8;
            record0[a + 0x46] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(record0.AsSpan(a + 0x48), 0xFFFFFFFF);
            Stamp(record0, usn: 1);

            record0.CopyTo(image, RecordOffset(0));
            foreach (var (number, record) in records)
            {
                record.CopyTo(image, RecordOffset(number));
            }

            image[Decoy] = 0x07;   //looks like a stamped empty record, but is not in the MFT
            return image;
        }

        /// <summary>The driver's stamp on a record that has an update sequence array: the USN goes into the array's
        /// first slot and the last two bytes of every sector, whose original bytes move into the array.</summary>
        public static void Stamp(byte[] record, ushort usn)
        {
            var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(arrayOffset), usn);
            for (var i = 1; i <= record.Length / BytesPerSector; i++)
            {
                var tail = i * BytesPerSector - 2;
                record[arrayOffset + 2 * i] = record[tail];
                record[arrayOffset + 2 * i + 1] = record[tail + 1];
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(tail), usn);
            }
        }

        /// <summary>A zeroed record after the driver stamped it: its array offset is 0, so the USN lands in bytes 0-1
        /// (the "signature") and at the end of each sector - the gh #79 record.</summary>
        public static byte[] ZeroedStamped(ushort usn)
        {
            var record = new byte[RecordSize];
            Stamp(record, usn);
            return record;
        }

        /// <summary>A live record: FILE, stamped, with no attributes.</summary>
        public static byte[] FileRecord(ushort usn)
        {
            var record = new byte[RecordSize];
            "FILE"u8.CopyTo(record);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 0x30);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14), 0x38);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x1C), RecordSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x38), 0xFFFFFFFF);
            Stamp(record, usn);
            return record;
        }

        public static byte[] Signature(string signature)
        {
            var record = new byte[RecordSize];
            System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(record, 0);
            return record;
        }
    }
}
