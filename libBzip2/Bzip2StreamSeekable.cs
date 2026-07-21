using libDecompression;
using libCommon;
using libCommon.Streams;
using SharpCompress.Compressors.BZip2;
using System.Diagnostics;
using Newtonsoft.Json;
using Serilog;
using libCommon.Lists;
using libCommon.Streams.Sparse;

namespace libBzip2
{
    public class Bzip2StreamSeekable : SeekableDecompressingStream
    {
        readonly LazyList<Mapping> blocks;
        public override IList<Mapping> Blocks => blocks;

        public Stream CompressedInputStream { get; }
        public bool ProcessTrailingNulls { get; }

        readonly byte[] FileHeader = new byte[4];

        //invariant for a read-only source; hoisted so the parallel block decoders never touch the
        //shared view's Length in their hot path
        readonly long compressedLength;

        public Bzip2StreamSeekable(Stream compressedInputStream, string? indexFilename, bool processTrailingNulls)
        {
            CompressedInputStream = compressedInputStream;
            sharedSource = new SharedStream(compressedInputStream);
            ProcessTrailingNulls = processTrailingNulls;
            compressedLength = compressedInputStream.Length;

            compressedInputStream.Seek(0, SeekOrigin.Begin);
            FileHeader = new byte[4];
            compressedInputStream.ReadExactly(FileHeader);

            //Read all the blocks synchronously
            //blocks = GetIndexContent(compressedInputStream, indexFilename).ToList();

            //Read all the blocks Lazily
            blocks = new LazyList<Mapping>(GetIndexContent(compressedInputStream, indexFilename));

            //Ideally, as soon as data is available (in the blocks LazyList), we would feed it to 7z.
            //However, 7z sometimes seeks multiple GB, which takes longer than 60 seconds to seek lazily, which results in 7z failing with "Insufficient system resources".
            //Read the first block, so that the reader at least has the magic bytes available
            //_ = blocks[0];

            //Let's evaluate 99% of the compressed file. This might be, say 40GB out of a 2TB image, as there tends to be a lot of trailing nulls which take a long time to unpack and aren't needed by 7z.
            //This way we'll process the 40GB fairly quickly, allowing 7z to start processing the content without waiting for the whole file to be unpacked.
            //Update - unfortunately still results in "Insufficient system resources" at the very end of opening the archive.
            /*
            foreach (var _ in blocks)
            {
                var per = compressedInputStream.Position / (double)compressedInputStream.Length * 100;
                Log.Debug($"Forced eval up to {per:N1}%");
                if (per > 98)
                {
                    break;
                }
            }
            */

            //Force eval of entire lazy list
            foreach (var _ in blocks)
            {
            }

            //Batch 8: group consecutive blocks into ~32 MB spans. GetRecommendation returns a whole
            //group, and Read decodes a group's blocks in parallel - bzip2 blocks are independently
            //decodable, so serving throughput scales with cores instead of being pinned to one.
            //A group only closes at a block boundary; a single merged entry larger than the target
            //(byte-aligned magic detection can merge many real blocks into one index entry) forms a
            //group by itself, which matches the old one-entry-per-recommendation behaviour.
            Mapping? currentGroup = null;
            foreach (var block in blocks)
            {
                if (currentGroup != null && (currentGroup.UncompressedEndByte - currentGroup.UncompressedStartByte) + (block.UncompressedEndByte - block.UncompressedStartByte) > TargetGroupBytes)
                {
                    blockGroups.Add(currentGroup);
                    currentGroup = null;
                }

                if (currentGroup == null)
                {
                    currentGroup = new Mapping()
                    {
                        CompressedStartByte = block.CompressedStartByte,
                        CompressedEndByte = block.CompressedEndByte,
                        UncompressedStartByte = block.UncompressedStartByte,
                        UncompressedEndByte = block.UncompressedEndByte,
                    };
                }
                else
                {
                    currentGroup.CompressedEndByte = block.CompressedEndByte;
                    currentGroup.UncompressedEndByte = block.UncompressedEndByte;
                }
            }
            if (currentGroup != null)
            {
                blockGroups.Add(currentGroup);
            }
        }

        const long TargetGroupBytes = 32L * 1024 * 1024;
        readonly List<Mapping> blockGroups = [];

        public override (long Start, long End) GetRecommendation(long start)
        {
            var group = blockGroups.BinarySearch(start, MappingComparer)
                ?? throw new Exception($"Could not find block group which contains position {start:N0}");

            return (group.UncompressedStartByte, group.UncompressedEndByte);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var pos = Position;
            var bytesLeftInFile = Length - pos;
            if (bytesLeftInFile <= 0 || count <= 0) return 0;
            var toRead = (int)Math.Min(count, bytesLeftInFile);

            var firstIndex = FindBlockIndex(pos);
            if (firstIndex < 0) return 0;
            var lastIndex = FindBlockIndex(pos + toRead - 1);
            if (lastIndex < 0) lastIndex = Blocks.Count - 1;

            int totalRead;
            if (firstIndex == lastIndex)
            {
                //single block - decode inline, no parallel overhead
                var block = Blocks[firstIndex];
                totalRead = DecodeBlock(block, pos - block.UncompressedStartByte, buffer, offset, toRead);
            }
            else
            {
                //multiple blocks - decode them concurrently, each into its own slice of the buffer.
                //Workers use independent SharedStream views, so compressed-file access is safe.
                var expected = new int[lastIndex - firstIndex + 1];
                var actual = new int[expected.Length];

                Parallel.For(firstIndex, lastIndex + 1, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                {
                    var block = Blocks[i];
                    var sliceStart = Math.Max(pos, block.UncompressedStartByte);
                    var sliceEnd = Math.Min(pos + toRead, block.UncompressedEndByte);
                    var sliceLength = (int)(sliceEnd - sliceStart);

                    expected[i - firstIndex] = sliceLength;
                    actual[i - firstIndex] = DecodeBlock(block, sliceStart - block.UncompressedStartByte, buffer, offset + (int)(sliceStart - pos), sliceLength);
                });

                //only the contiguous prefix counts; a short block decode invalidates everything after it
                totalRead = 0;
                for (var i = 0; i < actual.Length; i++)
                {
                    totalRead += actual[i];
                    if (actual[i] < expected[i]) break;
                }
            }

            Position = pos + totalRead;
            return totalRead;
        }

        int FindBlockIndex(long uncompressedPosition)
        {
            var lo = 0;
            var hi = Blocks.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var block = Blocks[mid];
                if (uncompressedPosition < block.UncompressedStartByte) hi = mid - 1;
                else if (uncompressedPosition >= block.UncompressedEndByte) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        //public override long UncompressedTotalLength => Blocks.Last().UncompressedEndByte;

        public override long UncompressedTotalLength
        {
            get
            {
                //We've been asked for the total length of the stream. We'll just respond with what we've discovered so far, allowing things like the 'list files' functions to start working before the entire file has been indexed.
                var lastIndexSoFar = blocks.CountSoFar - 1;

                var result = 0L;
                if (lastIndexSoFar >= 0)
                {
                    result = blocks[lastIndexSoFar].UncompressedEndByte;
                }

                return result;
            }
        }

        readonly SharedStream sharedSource;
        public override int ReadFromChunk(Mapping block, byte[] buffer, int offset, int count)
        {
            var positionInBlock = Position - block.UncompressedStartByte;
            return DecodeBlock(block, positionInBlock, buffer, offset, count);
        }

        /// <summary>
        /// Decodes one index entry: reconstructs a standalone single-block .bz2 image in memory
        /// (bit-shifting the block onto a byte boundary behind the 4-byte file header, see
        /// <see cref="BuildStandaloneBlockBytes"/>), decodes it, skips <paramref name="skipBytes"/> of
        /// decompressed output, then fills <paramref name="count"/> bytes. Thread-safe: each call uses
        /// its own SharedStream view and its own in-memory image, so parallel callers can decode
        /// different entries concurrently. A wrong reconstruction fails the block's CRC (loud), never
        /// returns silent garbage.
        /// </summary>
        int DecodeBlock(Mapping block, long skipBytes, byte[] buffer, int offset, int count)
        {
            var sourceView = sharedSource.CreateView();
            var (startBit, endBit) = BlockBitRange(block, compressedLength);
            var standalone = BuildStandaloneBlockBytes(sourceView, startBit, endBit);

            var decompressor = BZip2Stream.Create(new MemoryStream(standalone), SharpCompress.Compressors.CompressionMode.Decompress, false, tolerateTruncatedStream: true);

            //read and discard anything before the requested position
            if (skipBytes > 0)
            {
                Debug.WriteLine($"Skipping {skipBytes.BytesToString()} to read {count.BytesToString()} from block starting at {block.UncompressedStartByte.BytesToString()}");
                decompressor.CopyTo(Null, skipBytes, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
            }

            var totalRead = 0;
            while (totalRead < count)
            {
                var n = decompressor.Read(buffer, offset + totalRead, count - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
            decompressor.Close();

            return totalRead;
        }

        /// <summary>Absolute [startBit, endBit) of an entry's compressed bits, clamped to the stream.
        /// Byte-aligned entries (gzip-style / old bzip2 indexes) have zero bit-in-byte offsets, so this
        /// degenerates to whole-byte bounds. Pre-CompressedEndByte indexes fall back to the old
        /// generous UncompressedEndByte bound.</summary>
        static (long StartBit, long EndBit) BlockBitRange(Mapping block, long streamLengthBytes)
        {
            var startBit = block.CompressedStartByte * 8L + block.CompressedStartBitInByte;

            long endBit;
            var hasEnd = block.CompressedEndByte > block.CompressedStartByte
                || (block.CompressedEndByte == block.CompressedStartByte && block.CompressedEndBitInByte > block.CompressedStartBitInByte);
            if (hasEnd)
                endBit = block.CompressedEndByte * 8L + block.CompressedEndBitInByte;
            else
                endBit = block.UncompressedEndByte * 8L;   //ancient-index fallback (generous; clamped below)

            endBit = Math.Min(endBit, streamLengthBytes * 8L);
            return (startBit, endBit);
        }

        /// <summary>
        /// Reads the compressed bits [startBit, endBit) and re-emits them as a valid standalone .bz2
        /// byte array: the 4-byte file header ("BZh" + level) followed by the block's bits shifted so
        /// the block-start magic lands on the byte-4 boundary, zero-padded to a byte. This is the
        /// bzip2recover technique - it lets an unmodified decoder read a block that started at any bit
        /// offset in the original stream.
        /// </summary>
        //serve path: read the block's bytes from the stream, then assemble
        byte[] BuildStandaloneBlockBytes(Stream view, long startBit, long endBit)
        {
            var startByte = startBit >> 3;
            var numBits = endBit - startBit;
            var numOutBytes = (int)((numBits + 7) >> 3);

            //raw bytes covering the block, plus one for the shift lookahead
            var rawEndExclusive = Math.Min(view.Length, startByte + numOutBytes + 1);
            var rawLen = (int)(rawEndExclusive - startByte);
            var raw = new byte[rawLen];
            view.Seek(startByte, SeekOrigin.Begin);
            view.ReadExactly(raw, 0, rawLen);

            return AssembleStandalone(raw, 0, startBit, endBit);
        }

        //build path: the finder already handed us the block's bytes (starting at its start byte),
        //so no stream read is needed - this is what turns the build into one sequential pass
        byte[] BuildStandaloneBlockBytes(byte[] raw, long startBit, long endBit)
            => AssembleStandalone(raw, 0, startBit, endBit);

        /// <summary>Bit-shifts the block's bits (starting at <paramref name="startBit"/>, sourced from
        /// <paramref name="raw"/> at <paramref name="rawOffset"/>) onto the byte-4 boundary behind the
        /// file header, zero-padding the final byte. See <see cref="BuildStandaloneBlockBytes(Stream,long,long)"/>.</summary>
        byte[] AssembleStandalone(byte[] raw, int rawOffset, long startBit, long endBit)
        {
            var shift = (int)(startBit & 7);
            var numBits = endBit - startBit;
            var numOutBytes = (int)((numBits + 7) >> 3);

            var outBuf = new byte[FileHeader.Length + numOutBytes];
            Array.Copy(FileHeader, outBuf, FileHeader.Length);

            if (shift == 0)
            {
                Array.Copy(raw, rawOffset, outBuf, FileHeader.Length, Math.Min(numOutBytes, raw.Length - rawOffset));
            }
            else
            {
                for (var k = 0; k < numOutBytes; k++)
                {
                    var hi = (raw[rawOffset + k] << shift) & 0xFF;
                    var lo = (rawOffset + k + 1 < raw.Length) ? (raw[rawOffset + k + 1] >> (8 - shift)) : 0;
                    outBuf[FileHeader.Length + k] = (byte)(hi | lo);
                }
            }

            //zero the bits past the block's end in the final output byte, so trailing padding can
            //never be mistaken for the next magic
            var validBitsInLastByte = (int)(numBits & 7);
            if (validBitsInLastByte != 0 && numOutBytes > 0)
            {
                var mask = (byte)(0xFF << (8 - validBitsInLastByte));
                outBuf[^1] &= mask;
            }

            return outBuf;
        }

        public IEnumerable<Mapping> GetIndexContent(Stream inputStream, string? indexFilename)
        {
            if (indexFilename != null && File.Exists(indexFilename))
            {
                Log.Information($"Loading bzip2 index from {Path.GetFileName(indexFilename)}.");

                var json = File.ReadAllText(indexFilename);
                var res = JsonConvert.DeserializeObject<List<Mapping>>(json);
                if (res != null)
                {
                    return res;
                }
            }

            Log.Information($"Creating bzip2 index.");

            var finderView = sharedSource.CreateView();
            var streamLength = finderView.Length;

            //One sequential pass over the compressed file (the finder) feeds a BOUNDED queue that
            //parallel decoders drain. The finder is the only reader of the stream, so there are no
            //per-block seeks and no SharedStream-gate contention (the old approach did one gated
            //seek+read per block - ~8x more of them after bit-alignment, which serialised the whole
            //read phase). The bound back-pressures the finder: it outruns decoding, so an unbounded
            //queue would hold the entire compressed file in RAM.
            var dop = Environment.ProcessorCount;
            var queue = new System.Collections.Concurrent.BlockingCollection<(int Seq, byte[] Standalone, bool IsLast)>(2 * dop);
            var lengths = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
            var metadata = new System.Collections.Concurrent.ConcurrentDictionary<int, (long StartBit, long EndBit, bool IsLast)>();

            //Dedup identical blocks: a disk image's multi-TB tail of zeros becomes tens of thousands
            //of identical ~46 MB null blocks (bzip2 is deterministic, so equal content compresses to
            //equal bits; the bit-shift into the standalone image normalises away their differing bit
            //alignments). Consecutive blocks with a byte-identical standalone therefore decode to the
            //same length, so we decode ONE and reuse the length for the run - collapsing the tail's
            //decode from ~43k blocks to ~1. Deterministic (equal bytes provably decode equally), not a
            //heuristic. The finder is sequential, so the producer sees the run in order.
            var duplicateOf = new Dictionary<int, int>();

            var producer = Task.Factory.StartNew(() =>
            {
                try
                {
                    var seq = 0;
                    byte[]? prevStandalone = null;
                    var prevDistinctSeq = -1;
                    foreach (var block in BZip2BlockFinder.FindBlocksBitAlignedWithBytes(finderView, streamLength))
                    {
                        metadata[seq] = (block.StartBit, block.EndBit, block.IsLast);
                        var standalone = BuildStandaloneBlockBytes(block.Raw, block.StartBit, block.EndBit);

                        //the final (often partial) block is always decoded - it carries the trailing-null cap
                        if (prevStandalone != null && !block.IsLast && ((ReadOnlySpan<byte>)standalone).SequenceEqual(prevStandalone))
                        {
                            duplicateOf[seq] = prevDistinctSeq;
                        }
                        else
                        {
                            queue.Add((seq, standalone, block.IsLast));
                            prevStandalone = standalone;
                            prevDistinctSeq = seq;
                        }
                        seq++;
                    }
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, TaskCreationOptions.LongRunning);

            var totalDecoded = 0L;
            var nextProgressAt = 1L * 1024 * 1024 * 1024;
            var progressLock = new object();

            var consumers = Enumerable.Range(0, dop).Select(_ => Task.Run(() =>
            {
                foreach (var item in queue.GetConsumingEnumerable())
                {
                    using var decompressor = BZip2Stream.Create(new MemoryStream(item.Standalone), SharpCompress.Compressors.CompressionMode.Decompress, false, tolerateTruncatedStream: true);

                    var blockUncompressedLength = MeasureBlockLength(decompressor, item.IsLast);
                    lengths[item.Seq] = blockUncompressedLength;

                    var runningTotal = Interlocked.Add(ref totalDecoded, blockUncompressedLength);
                    if (runningTotal >= Volatile.Read(ref nextProgressAt))
                    {
                        lock (progressLock)
                        {
                            if (runningTotal >= nextProgressAt)
                            {
                                var m = metadata[item.Seq];
                                var percentThroughCompressedSource = (double)(m.EndBit >> 3) / streamLength * 100;
                                Log.Information($"Indexed {runningTotal.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");
                                nextProgressAt = (runningTotal / (1024 * 1024 * 1024) + 1) * 1024 * 1024 * 1024;
                            }
                        }
                    }
                }
            })).ToArray();

            Task.WaitAll(consumers);
            producer.Wait();   //surface any exception from the finder

            //stitch the (independently measured) block lengths back into ordered, prefix-summed offsets;
            //deduped blocks reuse the length of the distinct block they matched
            var count = metadata.Count;
            var blocks = new List<Mapping>(count);
            long uncompressedStartPos = 0L;
            for (var i = 0; i < count; i++)
            {
                var m = metadata[i];
                var length = duplicateOf.TryGetValue(i, out var leader) ? lengths[leader] : lengths[i];
                var blockInfo = new Mapping()
                {
                    CompressedStartByte = m.StartBit >> 3,
                    CompressedStartBitInByte = (int)(m.StartBit & 7),
                    CompressedEndByte = m.EndBit >> 3,
                    CompressedEndBitInByte = (int)(m.EndBit & 7),
                    UncompressedStartByte = uncompressedStartPos,
                    UncompressedEndByte = uncompressedStartPos + length
                };
                blocks.Add(blockInfo);
                uncompressedStartPos = blockInfo.UncompressedEndByte;
            }

            if (indexFilename != null)
            {
                Log.Information($"Finished generating bzip2 index. Saving to final location: {indexFilename}");
                File.WriteAllText(indexFilename, JsonConvert.SerializeObject(blocks, Formatting.Indented));
            }

            return blocks;
        }

        /// <summary>Fully decodes a reconstructed block to measure its decompressed length. The final
        /// block of an image (unless <see cref="ProcessTrailingNulls"/>) is capped once it has yielded
        /// &gt;4 GB of trailing zeros, which no filesystem references.</summary>
        long MeasureBlockLength(Stream decompressor, bool isLast)
        {
            if (!ProcessTrailingNulls && isLast)
            {
                var blockUncompressedLength = 0L;
                var sequenceOfEmptyBytes = 0L;
                using var sparseAwareReader = new SparseAwareReader(decompressor);

                while (true)
                {
                    var read = sparseAwareReader.CopyTo(Null, Buffers.ARBITRARY_LARGE_SIZE_BUFFER, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
                    if (read == 0) break;

                    blockUncompressedLength += read;
                    sequenceOfEmptyBytes = sparseAwareReader.LatestReadWasAllNull ? sequenceOfEmptyBytes + read : 0;

                    if (sequenceOfEmptyBytes > 4L * 1024 * 1024 * 1024)
                    {
                        Log.Warning($"The final bzip2 block has more that 4GB of empty bytes. Assuming the rest of the file is empty.");
                        break;
                    }
                }
                return blockUncompressedLength;
            }

            using var positionTrackingStream = new PositionTrackerStream();
            decompressor.CopyTo(positionTrackingStream, Buffers.ARBITRARY_LARGE_SIZE_BUFFER);
            return positionTrackingStream.Length;
        }
    }
}