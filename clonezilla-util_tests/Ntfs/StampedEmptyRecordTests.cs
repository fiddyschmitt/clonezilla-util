using System.Buffers.Binary;

namespace clonezilla_util_tests.Ntfs
{
    // End to end, against the real 7-Zip: a copy of the WinRE partition image with one of its unused, zeroed MFT
    // records stamped the way the driver does (update sequence number 7 in bytes 0-1 and at the end of each sector -
    // the gh #79 record). 7-Zip refuses that volume outright; the exe must list it exactly as it lists the unmodified
    // image, while extract-partition-image must still write the stamped image byte for byte.
    [TestClass]
    [DoNotParallelize]
    public class StampedEmptyRecordTests : ExeTestBase
    {
        const string Image = @"E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img";
        const ushort Usn = 7;

        //runaway protection only: ~1 min (two listings of a 576 MB image, then extracting it)
        [TestMethod, Timeout(20 * 60_000)]
        public void StampedRecord_ListsLikeTheOriginal()
        {
            var work = NewTempFolder();
            try
            {
                var stamped = Path.Combine(work, "sda1-stamped.img");
                var record = WriteStampedCopy(stamped);

                var cache = Path.Combine(work, "cache");
                var originalList = RunExe(["list", "--input", Image, "--cache-folder", cache]).AssertSucceeded("list (original)");
                var stampedList = RunExe(["list", "--input", stamped, "--cache-folder", cache]).AssertSucceeded("list (stamped)");

                Assert.IsTrue(originalList.Records().Contains(@"2021-12-28_pb-devops1_sda1\partition0\Recovery\WindowsRE\ReAgent.xml"), "ReAgent.xml not listed from the original image");
                CollectionAssert.AreEqual(
                    originalList.Records().Select(r => r.Replace(@"2021-12-28_pb-devops1_sda1\", @"sda1-stamped\")).ToArray(),
                    stampedList.Records(),
                    "the stamped image must list exactly what the original lists");
                Assert.IsTrue(stampedList.Stderr.Contains($"MFT record {record:N0} is an unused, zeroed record stamped with update sequence number {Usn}"), $"the log should name the patched record. End of the log:{Environment.NewLine}{stampedList.LogTail()}");
                Assert.IsFalse(originalList.Stderr.Contains("stamped with update sequence number"), "nothing should be patched in the original image");

                //the verbatim promise: the extracted partition image is the stamped input, unchanged
                var output = Path.Combine(work, "out");
                RunExe(["extract-partition-image", "--input", stamped, "-o", output, "--cache-folder", cache]).AssertSucceeded("extract-partition-image");
                AssertFileMd5(Path.Combine(output, "partition0.img"), libCommon.Utility.CalculateMD5(stamped));
            }
            finally { TryDeleteFolder(work); }
        }

        //Copies the image with its first all-zero MFT record stamped; returns that record's number
        static long WriteStampedCopy(string stamped)
        {
            var image = File.ReadAllBytes(Image);
            var (recordSize, bytesPerSector, records) = LocateMftRecords(image);
            var target = records.FirstOrDefault(r => !image.AsSpan((int)r.Offset, recordSize).ContainsAnyExcept((byte)0));
            if (target == default) Assert.Inconclusive("the image has no zeroed MFT record to stamp");

            var offset = (int)target.Offset;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset), Usn);
            for (var i = 1; i <= recordSize / bytesPerSector; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(offset + i * bytesPerSector - 2), Usn);
            }
            File.WriteAllBytes(stamped, image);
            return target.Number;
        }

        //An independent, minimal reading of the MFT layout: boot sector, then record 0's unnamed $DATA runs
        static (int RecordSize, int BytesPerSector, List<(long Number, long Offset)> Records) LocateMftRecords(byte[] image)
        {
            Assert.IsTrue(image.AsSpan(3, 8).SequenceEqual("NTFS    "u8), "not an NTFS partition");
            var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x0B));
            var clusterSize = bytesPerSector * image[0x0D];
            var mftOffset = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(0x30)) * clusterSize;
            var clustersPerRecord = (sbyte)image[0x40];
            var recordSize = clustersPerRecord < 0 ? 1 << -clustersPerRecord : clustersPerRecord * clusterSize;

            var record0 = image.AsSpan((int)mftOffset, recordSize).ToArray();
            Assert.IsTrue(record0.AsSpan(0, 4).SequenceEqual("FILE"u8), "MFT record 0 is not a FILE record");
            var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(6));
            for (var i = 1; i < count; i++)
            {
                record0[i * bytesPerSector - 2] = record0[arrayOffset + 2 * i];
                record0[i * bytesPerSector - 1] = record0[arrayOffset + 2 * i + 1];
            }

            var records = new List<(long, long)>();
            int attribute = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(0x14));
            while (true)
            {
                var type = BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(attribute));
                Assert.AreNotEqual(0xFFFFFFFF, type, "MFT record 0 has no $DATA attribute");
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(attribute + 4));
                if (type == 0x80 && record0[attribute + 9] == 0)
                {
                    var dataSize = BinaryPrimitives.ReadInt64LittleEndian(record0.AsSpan(attribute + 0x30));
                    var p = attribute + BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(attribute + 0x20));
                    long lcn = 0, number = 0;
                    while (record0[p] != 0)
                    {
                        int lengthSize = record0[p] & 0xF, offsetSize = record0[p] >> 4;
                        p++;
                        long runLength = 0;
                        for (var i = lengthSize - 1; i >= 0; i--) runLength = (runLength << 8) | record0[p + i];
                        p += lengthSize;
                        long delta = (sbyte)record0[p + offsetSize - 1];
                        for (var i = offsetSize - 2; i >= 0; i--) delta = (delta << 8) | record0[p + i];
                        p += offsetSize;
                        lcn += delta;
                        for (long b = 0; b < runLength * clusterSize && number < dataSize / recordSize; b += recordSize, number++)
                        {
                            records.Add((number, lcn * clusterSize + b));
                        }
                    }
                    return (recordSize, bytesPerSector, records);
                }
                attribute += length;
            }
        }
    }
}
