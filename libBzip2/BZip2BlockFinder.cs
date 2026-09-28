using libCommon;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libBzip2
{
    public static class BZip2BlockFinder
    {
        const ulong BlockMagic48 = 0x314159265359UL;   //start-of-block marker, 48 bits
        const ulong Mask48 = 0xFFFFFFFFFFFFUL;

        /// <summary>
        /// Finds every bzip2 block boundary at BIT granularity, handing back each block's raw compressed bytes.
        /// bzip2 blocks are bit-packed with no padding between them, so a boundary lands on a byte boundary only
        /// ~1 in 8 times; a byte-aligned search would merge up to ~8 real blocks into one entry that can only
        /// decode single-threaded. Scanning every bit offset recovers every boundary, so each entry is one ~900 KB
        /// block - the granularity that lets serving parallelise and bounds the decode-and-discard a random read
        /// pays. A rolling 64-bit accumulator holds the last 8 bytes; after loading the byte at absolute index p,
        /// the 8 bit-alignments that newly complete are the 48-bit block-start magic at bit offset (8*p - 40 - s)
        /// for s in 0..7 (offsets below 32, inside the 4-byte stream header, are skipped).
        /// <paramref name="Raw"/> begins at the block's start byte (<c>StartBit &gt;&gt; 3</c>) and runs one byte
        /// past the block's end for the bit-shift lookahead, sliced from the single sequential scan so the caller
        /// decodes without seeking back: the index build is one sequential read, the finder is the sole reader of
        /// the stream, and decoders work purely from the handed-over bytes. Feed the results through a BOUNDED
        /// queue - the scan outruns decoding, so an unbounded buffer would hold the whole compressed file in RAM.
        /// (The byte-aligned and bytes-free variants of this scan, and their helpers, were removed as dead code.)
        /// </summary>
        public static IEnumerable<(long StartBit, long EndBit, bool IsLast, byte[] Raw)> FindBlocksBitAlignedWithBytes(Stream stream, long streamLength)
        {
            stream.Seek(0, SeekOrigin.Begin);

            var readBuf = new byte[1 * 1024 * 1024];
            ulong acc = 0;
            long p = 0;
            var filled = 0;

            //bytes retained from the in-progress block's start byte onward (blockBuf[0] == byte blockBufBase)
            var blockBuf = new byte[2 * 1024 * 1024];
            long blockBufBase = 0;
            var blockBufLen = 0;

            long? prevStartBit = null;
            long prevStartByte = 0;

            while (true)
            {
                var bytesRead = stream.Read(readBuf, 0, readBuf.Length);
                if (bytesRead == 0) break;

                for (var i = 0; i < bytesRead; i++)
                {
                    var b = readBuf[i];

                    if (blockBufLen == blockBuf.Length) Array.Resize(ref blockBuf, blockBuf.Length * 2);
                    blockBuf[blockBufLen++] = b;

                    acc = (acc << 8) | b;
                    if (filled < 8) filled++;

                    if (filled == 8)
                    {
                        for (var s = 0; s < 8; s++)
                        {
                            if (((acc >> s) & Mask48) == BlockMagic48)
                            {
                                var g = 8L * p - 40 - s;
                                if (g >= 32)
                                {
                                    if (prevStartBit != null)
                                    {
                                        //emit the completed block [prevStartBit, g), bytes starting at its start byte
                                        var rawEnd = Math.Min(streamLength, ((g + 7) >> 3) + 1);
                                        var srcOffset = (int)(prevStartByte - blockBufBase);
                                        var rawLen = (int)(rawEnd - prevStartByte);
                                        var raw = new byte[rawLen];
                                        Array.Copy(blockBuf, srcOffset, raw, 0, rawLen);
                                        yield return (prevStartBit.Value, g, false, raw);

                                        //drop everything before the next block's start byte
                                        var newBase = g >> 3;
                                        var drop = (int)(newBase - blockBufBase);
                                        if (drop > 0)
                                        {
                                            Array.Copy(blockBuf, drop, blockBuf, 0, blockBufLen - drop);
                                            blockBufLen -= drop;
                                            blockBufBase = newBase;
                                        }
                                    }
                                    prevStartBit = g;
                                    prevStartByte = g >> 3;
                                    break;   //at most one real magic per byte; avoid a second (coincidental) split corrupting the buffer
                                }
                            }
                        }
                    }

                    p++;
                }
            }

            if (prevStartBit != null)
            {
                var srcOffset = (int)(prevStartByte - blockBufBase);
                var rawLen = blockBufLen - srcOffset;
                var raw = new byte[rawLen];
                Array.Copy(blockBuf, srcOffset, raw, 0, rawLen);
                yield return (prevStartBit.Value, streamLength * 8L, true, raw);
            }
        }
    }
}
