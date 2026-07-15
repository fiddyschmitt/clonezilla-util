using libCommon;
using libDecompression.Utilities;
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
        private static readonly byte[] StartOfBlockMagic = [0x31, 0x41, 0x59, 0x26, 0x53, 0x59];    //31 41 59 26 53 59
        //private static readonly byte[] EndOfBlockMagic = [0x17, 0x72, 0x45, 0x38, 0x50, 0x90];      //17 72 45 38 50 90       //This 'end of block' marker doesn't seem to appear in the file

        const ulong BlockMagic48 = 0x314159265359UL;   //start-of-block marker, 48 bits
        const ulong Mask48 = 0xFFFFFFFFFFFFUL;

        public static IEnumerable<(long Start, long End, bool IsLast)> FindBlocks(Stream stream)
        {
            long? startOfBlock = null;
            foreach (var foundPosition in FindInstances(stream, StartOfBlockMagic))
            {
                if (startOfBlock != null)
                {
                    var endOfBlock = foundPosition;
                    //Debug.WriteLine($"{startOfBlock:N0} - {endOfBlock:N0}");
                    yield return (startOfBlock.Value, endOfBlock, false);
                    startOfBlock = null;
                }

                startOfBlock = foundPosition;
            }

            if (startOfBlock != null)
            {
                yield return (startOfBlock.Value, stream.Length, true);
            }
        }

        /// <summary>
        /// Finds block boundaries at BIT granularity. bzip2 blocks are bit-packed with no padding
        /// between them, so a block boundary lands on a byte boundary only ~1 in 8 times; the
        /// byte-aligned <see cref="FindBlocks"/> misses the other 7/8, merging up to ~8 real blocks
        /// (and, across an unlucky run, arbitrarily many) into one entry that can only decode
        /// single-threaded. Scanning every bit offset recovers every boundary, so each entry is one
        /// ~900 KB block - the granularity that lets serving parallelise and that bounds the
        /// decode-and-discard a random read pays. Yields (StartBit, EndBit, IsLast) in absolute bit
        /// offsets from the start of the compressed stream.
        /// </summary>
        public static IEnumerable<(long StartBit, long EndBit, bool IsLast)> FindBlocksBitAligned(Stream stream)
        {
            long? startBit = null;
            foreach (var magicBit in FindMagicBitOffsets(stream))
            {
                if (startBit != null)
                {
                    yield return (startBit.Value, magicBit, false);
                }
                startBit = magicBit;
            }

            if (startBit != null)
            {
                yield return (startBit.Value, stream.Length * 8L, true);
            }
        }

        /// <summary>
        /// Like <see cref="FindBlocksBitAligned"/>, but also hands back each block's raw compressed
        /// bytes (sliced from the single sequential scan) so the caller can decode without seeking
        /// back to the block. <paramref name="Raw"/> begins at the block's start byte
        /// (<c>StartBit &gt;&gt; 3</c>) and runs one byte past the block's end for the bit-shift
        /// lookahead. This turns the index build from ~N random gated seek+reads into one sequential
        /// read: the finder is the sole reader of the stream, and decoders work purely from the
        /// handed-over bytes. Feed the results through a BOUNDED queue - the scan outruns decoding, so
        /// an unbounded buffer would hold the whole compressed file in RAM.
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

        /// <summary>
        /// Yields the absolute bit offset (MSB-first, matching bzip2's bit order) of every occurrence
        /// of the 48-bit block-start magic. A rolling 64-bit accumulator holds the last 8 bytes; after
        /// loading the byte at absolute index p, the 8 bit-alignments that newly complete are the
        /// magic at bit offset (8*p - 40 - s) for s in 0..7. Offsets below 32 (inside the 4-byte
        /// stream header) are skipped.
        /// </summary>
        public static IEnumerable<long> FindMagicBitOffsets(Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);

            var buff = new byte[1 * 1024 * 1024];
            ulong acc = 0;
            long p = 0;        //absolute index of the byte currently being folded into acc
            var filled = 0;    //bytes folded so far (caps at 8, when the window is complete)

            while (true)
            {
                var bytesRead = stream.Read(buff, 0, buff.Length);
                if (bytesRead == 0) break;

                for (var i = 0; i < bytesRead; i++)
                {
                    acc = (acc << 8) | buff[i];
                    if (filled < 8) filled++;

                    if (filled == 8)
                    {
                        for (var s = 0; s < 8; s++)
                        {
                            if (((acc >> s) & Mask48) == BlockMagic48)
                            {
                                var g = 8L * p - 40 - s;
                                if (g >= 32) yield return g;
                            }
                        }
                    }

                    p++;
                }
            }
        }

        public static IEnumerable<long> FindInstances(this Stream stream, byte[] find)
        {
            //Build the Boyer-Moore tables once for this needle and reuse them across
            //every buffer subsection, rather than rebuilding them on each IndexOf call.
            var searcher = new BoyerMooreSearcher(find);

            var buff = new byte[1 * 1024 * 1024];
            var ms = new MemoryStream(buff);

            while (true)
            {
                var bufferPositionInStream = stream.Position;

                ms.Seek(0, SeekOrigin.Begin);
                var bytesRead = (int)stream.CopyTo(ms, buff.Length, buff.Length);
                if (bytesRead == 0) break;

                var positionThroughCurrentBuffer = 0;
                while (true)
                {
                    var bytesRemainingInBuffer = bytesRead - positionThroughCurrentBuffer;
                    var subsectionToSearch = new Span<byte>(buff, positionThroughCurrentBuffer, bytesRemainingInBuffer);
                    var foundPositionInBufferSubsection = searcher.IndexOf(subsectionToSearch);

                    if (foundPositionInBufferSubsection == -1)
                    {
                        break;
                    }
                    else
                    {
                        var foundPositionInStream = bufferPositionInStream + positionThroughCurrentBuffer + foundPositionInBufferSubsection;
                        yield return foundPositionInStream;

                        positionThroughCurrentBuffer = positionThroughCurrentBuffer + foundPositionInBufferSubsection + 1;
                    }
                }

                //just in case the search pattern is right on the border.
                //rewind one byte less than the pattern length - rewinding the full length would re-find a match that ended exactly on the buffer boundary
                if (stream.Position == stream.Length)
                {
                    break;
                }
                else
                {
                    stream.Seek(-(find.Length - 1), SeekOrigin.Current);
                }
            }
        }
    }
}
