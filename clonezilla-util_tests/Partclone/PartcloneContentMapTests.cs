using System;
using System.IO;
using System.Linq;
using System.Text;
using libPartclone;

namespace clonezilla_util_tests.Partclone
{
    // Tests the partclone content map (BitmapContentMap, via PartcloneStream). Synthesises valid
    // V1/V2 partclone images entirely in memory (no external / machine-bound files) and verifies the
    // restored bytes against ground truth, across many bitmap patterns, chunk sizes and random seeks.
    [TestClass]
    public class PartcloneContentMapTests
    {
        const int BlockSize = 64;

        // populated at start and end, runs that span checksum strips, interior empty gaps,
        // and runs whose length is not a multiple of blocks-per-checksum.
        const string Typical = "1111111111000001111111111111111111110000111110000000001";

        [TestMethod]
        public void V2_WithChecksum_Typical()
        {
            var pop = Bits(Typical);
            var (img, exp) = BuildV2(pop, BlockSize, blocksPerChecksum: 4, checksumSize: 4, deviceSize: pop.Length * BlockSize);
            AssertRestoresTo(img, exp, BlockSize);
        }

        [TestMethod]
        public void V2_NoChecksum_Typical()
        {
            // checksum mode NONE => blocks_per_checksum = 0, checksum_size = 0 (this used to divide by zero).
            var pop = Bits(Typical);
            var (img, exp) = BuildV2(pop, BlockSize, blocksPerChecksum: 0, checksumSize: 0, deviceSize: pop.Length * BlockSize);
            AssertRestoresTo(img, exp, BlockSize);
        }

        [TestMethod]
        public void V1_Typical()
        {
            var pop = Bits(Typical);
            var (img, exp) = BuildV1(pop, BlockSize, deviceSize: pop.Length * BlockSize);
            AssertRestoresTo(img, exp, BlockSize);
        }

        [TestMethod]
        public void V2_PartialLastBlock()
        {
            // device size is not a multiple of the block size -> the final block is partial.
            var pop = Bits("111110000111111");
            long deviceSize = pop.Length * BlockSize - 23;
            var (img, exp) = BuildV2(pop, BlockSize, blocksPerChecksum: 4, checksumSize: 4, deviceSize);
            AssertRestoresTo(img, exp, BlockSize);
        }

        [TestMethod]
        public void V2_DeviceLargerThanBitmap()
        {
            // The device can span more blocks than the file system bitmap (e.g. NTFS backup boot sector
            // past the last FS block). Those trailing blocks are not in the bitmap and must be zero-filled
            // up to the device size. (This is exactly what the real pb-devops1 sda1 image hit: device_size
            // covered one block more than totalBlocks, and the old arithmetic map stopped a block short.)
            foreach (var pattern in new[] { "1111100000111", "1111111111", "10101010101", "1" })
            {
                var pop = Bits(pattern);
                foreach (var extraBlocks in new[] { 1, 3, 17 })
                {
                    long deviceSize = (pop.Length + extraBlocks) * BlockSize;
                    var (img, exp) = BuildV2(pop, BlockSize, blocksPerChecksum: 4, checksumSize: 4, deviceSize);
                    AssertRestoresTo(img, exp, BlockSize);

                    var v1 = BuildV1(pop, BlockSize, deviceSize);
                    AssertRestoresTo(v1.image, v1.expected, BlockSize);
                }

                // and a trailing partial block beyond the bitmap
                long deviceSizePartial = (pop.Length + 2) * BlockSize - 9;
                var (imgP, expP) = BuildV2(pop, BlockSize, blocksPerChecksum: 4, checksumSize: 4, deviceSizePartial);
                AssertRestoresTo(imgP, expP, BlockSize);
            }
        }

        [TestMethod]
        public void V2_LargeBlocksPerChecksum_RunSpansManyStrips()
        {
            // one long populated run crossing several strips, then a long empty run.
            var pop = Bits(new string('1', 70) + new string('0', 40) + new string('1', 13));
            var (img, exp) = BuildV2(pop, BlockSize, blocksPerChecksum: 8, checksumSize: 8, deviceSize: pop.Length * BlockSize);
            AssertRestoresTo(img, exp, BlockSize);
        }

        [TestMethod]
        public void EdgePatterns()
        {
            foreach (var pattern in new[]
            {
                "1", "0", "1111111111", "0000000000", "10101010101010", "0000011111", "1111100000",
            })
            {
                var pop = Bits(pattern);
                long deviceSize = pop.Length * BlockSize;

                var v2 = BuildV2(pop, BlockSize, blocksPerChecksum: 4, checksumSize: 4, deviceSize);
                AssertRestoresTo(v2.image, v2.expected, BlockSize);

                var v2nc = BuildV2(pop, BlockSize, blocksPerChecksum: 0, checksumSize: 0, deviceSize);
                AssertRestoresTo(v2nc.image, v2nc.expected, BlockSize);

                var v1 = BuildV1(pop, BlockSize, deviceSize);
                AssertRestoresTo(v1.image, v1.expected, BlockSize);
            }
        }

        // ---- assertion: the restored stream must equal the ground-truth bytes ----------------

        static void AssertRestoresTo(byte[] image, byte[] expected, int blockSize)
        {
            var chunkSizes = new[] { 1, 3, blockSize - 1, blockSize, blockSize + 1, blockSize * 3 + 1, 997 }
                .Where(c => c > 0)
                .Distinct()
                .ToArray();

            foreach (var chunk in chunkSizes)
            {
                using var ps = new PartcloneStream("container", "partition", new MemoryStream(image));
                Assert.AreEqual(expected.Length, ps.Length, $"Length mismatch (chunk={chunk})");

                var actual = ReadStreamFully(ps, chunk);
                CollectionAssert.AreEqual(expected, actual, $"Full sequential read mismatch (chunk={chunk})");
            }

            // random seeks must reproduce the ground-truth slice
            var rnd = new Random(98765);
            using var stream = new PartcloneStream("container", "partition", new MemoryStream(image));
            for (int i = 0; i < 1500; i++)
            {
                long pos = rnd.Next(0, (int)expected.Length + 5);
                int len = rnd.Next(1, blockSize * 4 + 5);
                CollectionAssert.AreEqual(Slice(expected, pos, len), ReadRange(stream, pos, len), $"Random-seek mismatch (pos={pos}, len={len})");
            }
        }

        static byte[] ReadStreamFully(PartcloneStream stream, int chunk)
        {
            stream.Seek(0, SeekOrigin.Begin);
            using var output = new MemoryStream();
            var buffer = new byte[chunk];
            while (true)
            {
                int n = stream.Read(buffer, 0, chunk);
                if (n == 0) break;
                output.Write(buffer, 0, n);
            }
            return output.ToArray();
        }

        // read exactly `len` bytes (or until EOF) from `pos`, looping over partial reads
        static byte[] ReadRange(PartcloneStream stream, long pos, int len)
        {
            stream.Seek(pos, SeekOrigin.Begin);
            using var output = new MemoryStream();
            var buffer = new byte[len];
            int remaining = len;
            while (remaining > 0)
            {
                int n = stream.Read(buffer, 0, remaining);
                if (n == 0) break;
                output.Write(buffer, 0, n);
                remaining -= n;
            }
            return output.ToArray();
        }

        static byte[] Slice(byte[] source, long pos, int len)
        {
            if (pos >= source.Length) return Array.Empty<byte>();
            int available = (int)Math.Min(len, source.Length - pos);
            var result = new byte[available];
            Array.Copy(source, pos, result, 0, available);
            return result;
        }

        // ---- synthetic image builders -------------------------------------------------------

        static bool[] Bits(string pattern) => pattern.Select(c => c == '1').ToArray();

        // deterministic, distinct-per-block content so ground truth is verifiable
        static byte BlockByte(long block, long intra) => (byte)((block * 131 + intra * 7 + 17) & 0xFF);

        static byte[] BuildExpected(bool[] populated, int blockSize, long deviceSize)
        {
            var expected = new byte[deviceSize];
            for (long pos = 0; pos < deviceSize; pos++)
            {
                long block = pos / blockSize;
                long intra = pos % blockSize;
                expected[pos] = (block < populated.Length && populated[block]) ? BlockByte(block, intra) : (byte)0;
            }
            return expected;
        }

        static byte[] Ascii(string text, int length)
        {
            var bytes = new byte[length];
            var src = Encoding.ASCII.GetBytes(text);
            Array.Copy(src, bytes, Math.Min(src.Length, length));
            return bytes;
        }

        static (byte[] image, byte[] expected) BuildV2(bool[] populated, int blockSize, int blocksPerChecksum, int checksumSize, long deviceSize)
        {
            int totalBlocks = populated.Length;
            ulong used = (ulong)populated.Count(p => p);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // ImageHeadV2 (magic[16] + ptc_version[14] + version[4] + endianess[2])
            bw.Write(Ascii("partclone-image", 16));
            bw.Write(Ascii("2.6.6", 14));
            bw.Write(Ascii("0002", 4));
            bw.Write((ushort)0xC0DE); // little-endian marker

            // FileSystemInfoV2 (fs[16] + device[8] + total[8] + superblockUsed[8] + bitmapUsed[8] + blockSize[4])
            bw.Write(Ascii("EXTFS", 16));
            bw.Write((ulong)deviceSize);
            bw.Write((ulong)totalBlocks);
            bw.Write(used);
            bw.Write(used);
            bw.Write((uint)blockSize);

            // ImageOptionsV2 (feature[4] + imgVer[2] + cpu[2] + csMode[2] + csSize[2] + bpc[4] + reseed[1] + bitmapMode[1])
            bw.Write((uint)18);
            bw.Write((ushort)2);
            bw.Write((ushort)64);
            bw.Write((ushort)(checksumSize == 0 ? 0x00 : 0x20)); // CSM_NONE / CSM_CRC32
            bw.Write((ushort)checksumSize);
            bw.Write((uint)blocksPerChecksum);
            bw.Write((byte)0);     // reseed
            bw.Write((byte)0x01);  // BM_BIT
            bw.Write((uint)0);     // header CRC (ignored by the reader)

            // Bitmap: ceil(totalBlocks/8) bytes, 1 bit/block, LSB-first
            var bitmap = new byte[(totalBlocks + 7) / 8];
            for (int b = 0; b < totalBlocks; b++)
            {
                if (populated[b]) bitmap[b >> 3] |= (byte)(1 << (b & 7));
            }
            bw.Write(bitmap);
            bw.Write((uint)0);     // bitmap CRC (ignored by the reader)

            WriteContent(bw, populated, blockSize, blocksPerChecksum, checksumSize);

            bw.Flush();
            return (ms.ToArray(), BuildExpected(populated, blockSize, deviceSize));
        }

        static (byte[] image, byte[] expected) BuildV1(bool[] populated, int blockSize, long deviceSize)
        {
            int totalBlocks = populated.Length;

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // ImageHeadV1 (magic[15] + fs[15] + version[4] + padding[2])
            bw.Write(Ascii("partclone-image", 15));
            bw.Write(Ascii("EXTFS", 15));
            bw.Write(Ascii("0001", 4));
            bw.Write(Ascii("", 2));

            // FileSystemInfoV1 (blockSize[4] + device[8] + total[8] + used[8])
            bw.Write((uint)blockSize);
            bw.Write((ulong)deviceSize);
            bw.Write((ulong)totalBlocks);
            bw.Write((ulong)populated.Count(p => p));

            // ImageOptionsV1 (buff[4096])
            bw.Write(new byte[4096]);

            // Bitmap: 1 byte/block (BM_BYTE)
            var bitmap = new byte[totalBlocks];
            for (int b = 0; b < totalBlocks; b++) bitmap[b] = (byte)(populated[b] ? 1 : 0);
            bw.Write(bitmap);

            // BiTmAgIc signature
            bw.Write(Ascii("BiTmAgIc", 8));

            // V1 always uses 1 block per checksum, 4-byte checksum
            WriteContent(bw, populated, blockSize, blocksPerChecksum: 1, checksumSize: 4);

            bw.Flush();
            return (ms.ToArray(), BuildExpected(populated, blockSize, deviceSize));
        }

        // populated blocks in order; a checksum follows each complete strip of blocksPerChecksum blocks
        static void WriteContent(BinaryWriter bw, bool[] populated, int blockSize, int blocksPerChecksum, int checksumSize)
        {
            long writtenPopulated = 0;
            for (int b = 0; b < populated.Length; b++)
            {
                if (!populated[b]) continue;

                for (int i = 0; i < blockSize; i++) bw.Write(BlockByte(b, i));
                writtenPopulated++;

                if (blocksPerChecksum != 0 && writtenPopulated % blocksPerChecksum == 0)
                {
                    bw.Write(new byte[checksumSize]); // checksum content is skipped on read
                }
            }
        }
    }
}
