using libDecompression;
using libCommon;
using libCommon.Streams;
using SharpCompress.Compressors.BZip2;
using System.Diagnostics;
using Newtonsoft.Json;
using Serilog;
using libCommon.Lists;

namespace libBzip2
{
    public class Bzip2StreamSeekable : SeekableDecompressingStream
    {
        readonly IList<Mapping> blocks = [];
        public override IList<Mapping> Blocks => blocks;

        public Stream CompressedInputStream { get; }

        readonly byte[] FileHeader = new byte[4];

        public Bzip2StreamSeekable(Stream compressedInputStream, string? indexFilename)
        {
            CompressedInputStream = compressedInputStream;

            compressedInputStream.Seek(0, SeekOrigin.Begin);
            FileHeader = new byte[4];
            compressedInputStream.Read(FileHeader, 0, FileHeader.Length);

            //This works, but Dokan immediately evaluates the whole list because it asks SeekableDecompressingStream for the Length property, which requires the last item in the list.
            //blocks = new LazyList<Mapping>(GetIndexContent(compressedInputStream, indexFilename));

            blocks = GetIndexContent(compressedInputStream, indexFilename).ToList();
        }

        readonly object sourceStreamLock = new();
        public override int ReadFromChunk(Mapping block, byte[] buffer, int offset, int count)
        {
            var independentStream = new IndependentStream(CompressedInputStream, sourceStreamLock);
            var compressedContent = new SubStream(independentStream, block.CompressedStartByte, block.UncompressedEndByte);

            var fileHeaderContent = new MemoryStream(FileHeader);
            var fullBlockContent = new Multistream(new Stream[] { fileHeaderContent, compressedContent });


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

            Position += bytesActuallyRead;

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
            var totalBytesUncompressed = 0L;
            var largestCompressedPositionProcessed = 0L;
            var largestCompressedPositionProcessedLock = new object();

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
                                var fullBlockContent = new Multistream(new Stream[] { fileHeaderContent, compressedContent });


                                using var bzip2Decompressor = new BZip2Stream(fullBlockContent, SharpCompress.Compressors.CompressionMode.Decompress, false);
                                using var positionTrackingStream = new PositionTrackerStream();
                                bzip2Decompressor.CopyTo(positionTrackingStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER,
                                    progress =>
                                    {
                                        lock (largestCompressedPositionProcessedLock)
                                        {
                                            totalBytesUncompressed += progress.Read;

                                            if (inputStream.Position > largestCompressedPositionProcessed)
                                            {
                                                largestCompressedPositionProcessed = inputStream.Position;
                                            }
                                        }

                                        var perThroughCompressedSource = (double)largestCompressedPositionProcessed / inputStream.Length * 100;

                                        Debug.WriteLine($"Decompressed {totalBytesUncompressed:N0} bytes");
                                        Log.Information($"Indexed {totalBytesUncompressed.BytesToString()}. ({perThroughCompressedSource:N1}% through source file)");
                                    });
                                positionTrackingStream.Seek(0, SeekOrigin.Begin);

                                return new
                                {
                                    Metadata = block,
                                    UncompressedLength = positionTrackingStream.Length
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

                                Debug.WriteLine($"{++blockCount:N0}    {blockInfo}");

                                uncompressedStartPos = blockInfo.UncompressedEndByte;

                                return blockInfo;
                            });

            Log.Information($"Finished generating bzip2 index. Saving to final location: {indexFilename}");

            if (indexFilename != null)
            {
                var json = JsonConvert.SerializeObject(result, Formatting.Indented);
                File.WriteAllText(indexFilename, json);
            }

            return result;
        }
    }
}