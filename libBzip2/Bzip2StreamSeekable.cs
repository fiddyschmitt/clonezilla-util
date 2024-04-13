using libDecompression;
using libCommon;
using libCommon.Streams;
using SharpCompress.Compressors.BZip2;
using System.Diagnostics;

namespace libBzip2
{
    public class Bzip2StreamSeekable : SeekableDecompressingStream
    {
        readonly List<Mapping> blocks = [];
        public override List<Mapping> Blocks => blocks;

        public Stream CompressedInputStream { get; }

        readonly byte[] FileHeader = new byte[4];

        public Bzip2StreamSeekable(Stream compressedInputStream, string indexFilename)
        {
            CompressedInputStream = compressedInputStream;

            compressedInputStream.Seek(0, SeekOrigin.Begin);
            FileHeader = new byte[4];
            compressedInputStream.Read(FileHeader, 0, FileHeader.Length);

            blocks = GetIndexContent(compressedInputStream, indexFilename);
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

        public List<Mapping> GetIndexContent(Stream inputStream, string indexFilename)
        {
            var independentInputStream = new IndependentStream(inputStream, sourceStreamLock);

            var result = new List<Mapping>();
            long uncompressedStartPos = 0L;


            var blockCount = 0;

            BZip2BlockFinder
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
                    using var ms = new MemoryStream();
                    bzip2Decompressor.CopyTo(ms);
                    ms.Seek(0, SeekOrigin.Begin);

                    return new
                    {
                        Metadata = block,
                        UncompressedLength = ms.Length
                    };
                }, Environment.ProcessorCount)
                .ForEach(block =>
                {
                    var blockInfo = new Mapping()
                    {
                        CompressedStartByte = block.Metadata.Start,
                        UncompressedStartByte = uncompressedStartPos,
                        UncompressedEndByte = uncompressedStartPos + block.UncompressedLength
                    };

                    Debug.WriteLine($"{++blockCount:N0}    {blockInfo}");

                    result.Add(blockInfo);

                    uncompressedStartPos = blockInfo.UncompressedEndByte;
                });

            return result;
        }
    }
}