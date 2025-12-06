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
            ProcessTrailingNulls = processTrailingNulls;

            compressedInputStream.Seek(0, SeekOrigin.Begin);
            FileHeader = new byte[4];
            compressedInputStream.Read(FileHeader, 0, FileHeader.Length);

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

        readonly object sourceStreamLock = new();
        public override int ReadFromChunk(Mapping block, byte[] buffer, int offset, int count)
        {
            var independentStream = new IndependentStream(CompressedInputStream, sourceStreamLock);
            var compressedContent = new SubStream(independentStream, block.CompressedStartByte, block.UncompressedEndByte);

            var fileHeaderContent = new MemoryStream(FileHeader);
            var fullBlockContent = new Multistream([fileHeaderContent, compressedContent]);


            //determine where we should start reading in the substream
            var positionInBlock = Position - block.UncompressedStartByte;

            var decompressor = new BZip2Stream(fullBlockContent, SharpCompress.Compressors.CompressionMode.Decompress, false);

            //read and discard anything before it
            if (positionInBlock > 0)
            {
                Debug.WriteLine($"Skipping {positionInBlock.BytesToString()} to read {count.BytesToString()} from position {Position.BytesToString()}");
                decompressor.CopyTo(Null, positionInBlock, Buffers.ARBITARY_MEDIUM_SIZE_BUFFER);
            }

            var bytesActuallyRead = decompressor.Read(buffer, offset, count);
            decompressor.Close();

            return bytesActuallyRead;
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

            var independentInputStream = new IndependentStream(inputStream, sourceStreamLock);

            long uncompressedStartPos = 0L;

            var blockCount = 0;
            var largestCompressedPositionProcessed = 0L;
            var largestCompressedPositionProcessedLock = new object();

            var blocks = new List<Mapping>();

            var result = BZip2BlockFinder
                            .FindBlocks(independentInputStream)
                            .SelectParallelPreserveOrder(block =>
                            {
                                var independentStream = new IndependentStream(inputStream, sourceStreamLock);

                                var compressedContent = new MemoryStream();
                                independentStream.Seek(block.Start, SeekOrigin.Begin);
                                independentStream.CopyTo(compressedContent, block.End - block.Start, Buffers.ARBITARY_MEDIUM_SIZE_BUFFER);
                                compressedContent.Seek(0, SeekOrigin.Begin);


                                var fileHeaderContent = new MemoryStream(FileHeader);
                                var fullBlockContent = new Multistream([fileHeaderContent, compressedContent]);

                                //var blockMD5 = Utility.CalculateMD5(fullBlockContent);
                                //Debug.WriteLine($"Block at {block.Start:N0} - {block.End:N0}: {blockMD5}");
                                //fullBlockContent.Seek(0, SeekOrigin.Begin);


                                using var bzip2Decompressor = new BZip2Stream(fullBlockContent, SharpCompress.Compressors.CompressionMode.Decompress, false);

                                var blockUncompressedLength = 0L;
                                if (!ProcessTrailingNulls && block.IsLast)
                                {
                                    //The last block in the archive tends to have a lot of trailing nulls, so we don't want to read the whole thing.

                                    var sequenceOfEmptyBytes = 0L;
                                    using var sparseAwareReader = new SparseAwareReader(bzip2Decompressor);

                                    while (true)
                                    {
                                        var read = sparseAwareReader.CopyTo(Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER, Buffers.ARBITARY_MEDIUM_SIZE_BUFFER);
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

                                    bzip2Decompressor.CopyTo(positionTrackingStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                                        progress =>
                                        {
                                            lock (largestCompressedPositionProcessedLock)
                                            {
                                                if (independentInputStream.Position > largestCompressedPositionProcessed)
                                                {
                                                    largestCompressedPositionProcessed = independentInputStream.Position;
                                                }
                                            }

                                            var percentThroughCompressedSource = (double)largestCompressedPositionProcessed / independentInputStream.Length * 100;

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
                                    CompressedStartByte = block.Metadata.Start,
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