using libPartclone.Metadata;
using System;
using System.Numerics;

namespace libPartclone
{
    //Arithmetic content map. Keeps the bitmap as packed 64-bit words plus a sparse
    //cumulative-popcount index, and derives the image offset of any output position with
    //the same formula partclone itself uses (src/fuseimg.c read_block_data and
    //src/partclone.c cnv_blocks_to_bytes / get_checksum_count):
    //
    //    used         = number of populated blocks before this block   (a bitmap rank query)
    //    contentOffset = startOfContent + used*blockSize + (used / blocksPerChecksum)*checksumSize
    //
    //Nothing is split per checksum strip, so memory is bounded by the bitmap size (1 bit/block)
    //instead of the data volume, and no precomputed range list needs to be built or cached.
    public sealed class BitmapContentMap : IPartcloneContentMap
    {
        const int WordBits = 64;

        //One cumulative-popcount checkpoint per this many words. Trades index size for the
        //number of PopCount ops in a rank query (<= this many per Locate call).
        const int CheckpointStrideWords = 256;

        readonly ulong[] words;
        readonly long[] rankCheckpoints;   //rankCheckpoints[k] = popcount of words[0 .. k*CheckpointStrideWords)
        readonly long totalBlocks;
        readonly long blockSize;
        readonly long startOfContent;
        readonly long checksumSize;
        readonly long blocksPerChecksum;   //0 => the image has no checksums
        readonly long totalLength;
        readonly long lastPopulatedBlock;  //-1 if no block is populated

        public BitmapContentMap(byte[] bitmap, BitmapMode bitmapMode, uint blockSize, ushort checksumSize, uint blocksPerChecksum, long startOfContent, ulong totalBlocks, long totalLength)
        {
            this.blockSize = blockSize;
            this.checksumSize = checksumSize;
            this.blocksPerChecksum = blocksPerChecksum;
            this.startOfContent = startOfContent;
            this.totalBlocks = (long)totalBlocks;
            this.totalLength = totalLength;

            words = PackBitmap(bitmap, bitmapMode, this.totalBlocks);

            //build cumulative-popcount checkpoints and locate the highest set bit
            rankCheckpoints = new long[(words.Length / CheckpointStrideWords) + 1];
            long running = 0;
            long highestNonZeroWord = -1;
            for (int w = 0; w < words.Length; w++)
            {
                if (w % CheckpointStrideWords == 0)
                {
                    rankCheckpoints[w / CheckpointStrideWords] = running;
                }

                ulong word = words[w];
                running += BitOperations.PopCount(word);
                if (word != 0) highestNonZeroWord = w;
            }

            if (highestNonZeroWord >= 0)
            {
                int topBit = (WordBits - 1) - BitOperations.LeadingZeroCount(words[highestNonZeroWord]);
                lastPopulatedBlock = highestNonZeroWord * WordBits + topBit;
            }
            else
            {
                lastPopulatedBlock = -1;
            }
        }

        static ulong[] PackBitmap(byte[] bitmap, BitmapMode bitmapMode, long totalBlocks)
        {
            switch (bitmapMode)
            {
                case BitmapMode.BM_BIT:
                    //bytes are already 1 bit per block, LSB-first within each byte (matching
                    //partclone's pc_test_bit on little-endian). On a little-endian platform the
                    //byte layout of the word array is identical, so a raw copy suffices.
                    var bitWords = new ulong[(bitmap.Length + 7) / 8];
                    Buffer.BlockCopy(bitmap, 0, bitWords, 0, bitmap.Length);
                    return bitWords;

                case BitmapMode.BM_BYTE:
                    //1 byte per block: any non-zero value means the block is present. Pack to bits.
                    long blocks = Math.Min(totalBlocks, bitmap.Length);
                    var byteWords = new ulong[(blocks + 63) / 64];
                    for (long b = 0; b < blocks; b++)
                    {
                        if (bitmap[b] != 0)
                        {
                            byteWords[b >> 6] |= 1UL << (int)(b & 63);
                        }
                    }
                    return byteWords;

                default:
                    throw new Exception($"Unsupported BitmapMode: {bitmapMode}");
            }
        }

        bool TestBit(long block) => (words[block >> 6] & (1UL << (int)(block & 63))) != 0;

        //number of populated blocks strictly before `block`
        long Used(long block)
        {
            long wordIdx = block >> 6;
            long checkpoint = wordIdx / CheckpointStrideWords;
            long used = rankCheckpoints[checkpoint];

            for (long w = checkpoint * CheckpointStrideWords; w < wordIdx; w++)
            {
                used += BitOperations.PopCount(words[w]);
            }

            int bit = (int)(block & 63);
            if (bit != 0)
            {
                used += BitOperations.PopCount(words[wordIdx] & ((1UL << bit) - 1));
            }

            return used;
        }

        //number of consecutive blocks equal to `value` starting at startBlock,
        //capped at maxBlocks (and at totalBlocks). Word-accelerated for homogeneous regions.
        long CountRun(long startBlock, bool value, long maxBlocks)
        {
            long limit = Math.Min(startBlock + maxBlocks, totalBlocks);
            long b = startBlock;

            while (b < limit && (b & 63) != 0)
            {
                if (TestBit(b) != value) return b - startBlock;
                b++;
            }

            ulong wholeWord = value ? ulong.MaxValue : 0UL;
            while (b + WordBits <= limit && words[b >> 6] == wholeWord) b += WordBits;

            while (b < limit && TestBit(b) == value) b++;

            return b - startBlock;
        }

        public ContentLocation Locate(long position, int count)
        {
            if (position < 0 || position >= totalLength || count <= 0)
            {
                return new ContentLocation { IsPopulated = false, ContentOffset = 0, Length = 0 };
            }

            long block = position / blockSize;
            long intra = position - block * blockSize;
            long want = Math.Min(count, totalLength - position);

            //The device can be larger than the file system: device_size may cover more blocks
            //than the bitmap (e.g. the NTFS backup boot sector past the last FS block). Blocks at
            //or beyond totalBlocks are not in the bitmap and are implicitly empty (zero-filled to
            //the device size), so don't probe the bitmap for them.
            bool populated = block < totalBlocks && TestBit(block);

            if (!populated)
            {
                long gapBytes;
                if (block >= totalBlocks)
                {
                    //entirely beyond the bitmap -> empty all the way to the device end
                    gapBytes = want;
                }
                else
                {
                    //zero-fill up to the next populated block, scanning only within the read window
                    long maxBlocks = (intra + want + blockSize - 1) / blockSize;
                    long gapBlocks = CountRun(block, false, maxBlocks);
                    if (block + gapBlocks >= totalBlocks)
                    {
                        //the empty run reached the end of the bitmap; everything beyond is empty too
                        gapBytes = want;
                    }
                    else
                    {
                        gapBytes = gapBlocks * blockSize - intra;
                    }
                }
                int emptyLen = (int)Math.Min(want, gapBytes);
                return new ContentLocation { IsPopulated = false, ContentOffset = 0, Length = emptyLen };
            }

            long used = Used(block);
            long checksums = blocksPerChecksum == 0 ? 0 : used / blocksPerChecksum;
            long contentOffset = startOfContent + used * blockSize + checksums * checksumSize + intra;

            //a checksum sits between strips, so a single read can't cross the strip boundary
            long stripBytes = long.MaxValue;
            if (blocksPerChecksum != 0)
            {
                long blocksLeftInStrip = blocksPerChecksum - (used % blocksPerChecksum);
                stripBytes = blocksLeftInStrip * blockSize - intra;
            }

            //and it can't read past the end of this populated run
            long bounded = Math.Min(want, stripBytes);
            long maxRunBlocks = (intra + bounded + blockSize - 1) / blockSize;
            long runBlocks = CountRun(block, true, maxRunBlocks);
            long runBytes = runBlocks * blockSize - intra;

            int len = (int)Math.Min(bounded, runBytes);
            return new ContentLocation { IsPopulated = true, ContentOffset = contentOffset, Length = len };
        }

        public bool RestIsAllNullFrom(long position)
        {
            if (position >= totalLength) return true;
            long block = position / blockSize;
            return block > lastPopulatedBlock;
        }
    }
}
