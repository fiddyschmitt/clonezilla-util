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

        public Bzip2StreamSeekable(Stream compressedInputStream, string? indexFilename, bool processTrailingNulls)
        {
            CompressedInputStream = compressedInputStream;
            sharedSource = new SharedStream(compressedInputStream);
            ProcessTrailingNulls = processTrailingNulls;

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
            var (startBit, endBit) = BlockBitRange(block, sourceView.Length);
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
        byte[] BuildStandaloneBlockBytes(Stream view, long startBit, long endBit)
        {
            var startByte = startBit >> 3;
            var shift = (int)(startBit & 7);
            var numBits = endBit - startBit;
            var numOutBytes = (int)((numBits + 7) >> 3);

            //raw bytes covering the block, plus one for the shift lookahead
            var rawEndExclusive = Math.Min(view.Length, startByte + numOutBytes + 1);
            var rawLen = (int)(rawEndExclusive - startByte);
            var raw = new byte[rawLen];
            view.Seek(startByte, SeekOrigin.Begin);
            view.ReadExactly(raw, 0, rawLen);

            var outBuf = new byte[FileHeader.Length + numOutBytes];
            Array.Copy(FileHeader, outBuf, FileHeader.Length);

            if (shift == 0)
            {
                Array.Copy(raw, 0, outBuf, FileHeader.Length, Math.Min(numOutBytes, rawLen));
            }
            else
            {
                for (var k = 0; k < numOutBytes; k++)
                {
                    var hi = (raw[k] << shift) & 0xFF;
                    var lo = (k + 1 < rawLen) ? (raw[k + 1] >> (8 - shift)) : 0;
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

            var progressView = sharedSource.CreateView();

            long uncompressedStartPos = 0L;

            var blockCount = 0;
            var largestCompressedPositionProcessed = 0L;
            var largestCompressedPositionProcessedLock = new object();

            var blocks = new List<Mapping>();

            var result = BZip2BlockFinder
                            .FindBlocksBitAligned(progressView)
                            .SelectParallelPreserveOrder(block =>
                            {
                                var blockView = sharedSource.CreateView();

                                //reconstruct the block as a standalone byte-aligned .bz2 image, then decode
                                var standalone = BuildStandaloneBlockBytes(blockView, block.StartBit, block.EndBit);

                                using var bzip2Decompressor = BZip2Stream.Create(new MemoryStream(standalone), SharpCompress.Compressors.CompressionMode.Decompress, false, tolerateTruncatedStream: true);

                                var blockUncompressedLength = 0L;
                                if (!ProcessTrailingNulls && block.IsLast)
                                {
                                    //The last block in the archive tends to have a lot of trailing nulls, so we don't want to read the whole thing.

                                    var sequenceOfEmptyBytes = 0L;
                                    using var sparseAwareReader = new SparseAwareReader(bzip2Decompressor);

                                    while (true)
                                    {
                                        var read = sparseAwareReader.CopyTo(Null, Buffers.ARBITRARY_LARGE_SIZE_BUFFER, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
                                        if (read == 0)
                                        {
                                            break;
                                        }

                                        blockUncompressedLength += read;

                                        if (sparseAwareReader.LatestReadWasAllNull)
                                        {
                                            sequenceOfEmptyBytes += read;
                                        }
                                        else
                                        {
                                            sequenceOfEmptyBytes = 0;
                                        }

                                        if (sequenceOfEmptyBytes > 4L * 1024 * 1024 * 1024)
                                        {
                                            Log.Warning($"The final bzip2 block has more that 4GB of empty bytes. Assuming the rest of the file is empty.");
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    using var positionTrackingStream = new PositionTrackerStream();

                                    bzip2Decompressor.CopyTo(positionTrackingStream, Buffers.ARBITRARY_LARGE_SIZE_BUFFER,
                                        progress =>
                                        {
                                            lock (largestCompressedPositionProcessedLock)
                                            {
                                                if (progressView.Position > largestCompressedPositionProcessed)
                                                {
                                                    largestCompressedPositionProcessed = progressView.Position;
                                                }
                                            }

                                            var percentThroughCompressedSource = (double)largestCompressedPositionProcessed / progressView.Length * 100;

                                            Log.Information($"Indexed {progress.TotalRead.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");
                                        });

                                    blockUncompressedLength = positionTrackingStream.Length;
                                }

                                return new
                                {
                                    Metadata = block,
                                    UncompressedLength = blockUncompressedLength
                                };
                            }, Environment.ProcessorCount)
                            .Select(block =>
                            {
                                var blockInfo = new Mapping()
                                {
                                    CompressedStartByte = block.Metadata.StartBit >> 3,
                                    CompressedStartBitInByte = (int)(block.Metadata.StartBit & 7),
                                    CompressedEndByte = block.Metadata.EndBit >> 3,
                                    CompressedEndBitInByte = (int)(block.Metadata.EndBit & 7),
                                    UncompressedStartByte = uncompressedStartPos,
                                    UncompressedEndByte = uncompressedStartPos + block.UncompressedLength
                                };
                                blocks.Add(blockInfo);

                                if (indexFilename != null && block.Metadata.IsLast)
                                {
                                    Log.Information($"Finished generating bzip2 index. Saving to final location: {indexFilename}");

                                    var json = JsonConvert.SerializeObject(blocks, Formatting.Indented);
                                    File.WriteAllText(indexFilename, json);
                                }

                                Debug.WriteLine($"{++blockCount:N0}    {blockInfo}");

                                uncompressedStartPos = blockInfo.UncompressedEndByte;

                                return blockInfo;
                            });

            return result;
        }
    }
}