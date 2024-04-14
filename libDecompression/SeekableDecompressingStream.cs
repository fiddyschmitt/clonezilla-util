using libCommon;
using libCommon.Streams;
using libDecompression.Lists;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDecompression
{
    public abstract class SeekableDecompressingStream : Stream, IReadSuggestor
    {
        public abstract IList<Mapping> Blocks { get; }

        long UncompressedTotalLength => Blocks.Last().UncompressedEndByte;
        public readonly MappingComparer MappingComparer = new();

        public abstract int ReadFromChunk(Mapping chunk, byte[] buffer, int offset, int count);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => UncompressedTotalLength;

        long position = 0;
        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush() => throw new NotImplementedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalBytesRead = 0;
            var leftToRead = count;
            while (leftToRead > 0)
            {
                var chunkDetails = Blocks.BinarySearch(position, MappingComparer);

                if (chunkDetails == null)
                {
                    break;
                }

                var bytesLeftInFile = Length - position;
                var bytesToRead = (int)Math.Min(leftToRead, bytesLeftInFile);

                int bytesRead = 0;
                if (chunkDetails != null)
                {
                    bytesRead = ReadFromChunk(chunkDetails, buffer, offset, bytesToRead);
                }

                position += bytesRead;
                totalBytesRead += bytesRead;
                leftToRead -= bytesRead;
            }

            return totalBytesRead;
        }

        public (long Start, long End) GetRecommendation(long start, long end)
        {
            end = Math.Min(Length, end);

            var startIndexPoint = Blocks.BinarySearch(start, MappingComparer) ?? throw new Exception($"Could not find block which contains position {start:N0}");
            var endIndexPoint = Blocks.BinarySearch(end, MappingComparer) ?? throw new Exception($"Could not find block which contains position {end:N0}");

            var recommendedStart = startIndexPoint.UncompressedStartByte;
            var recommendedEnd = endIndexPoint.UncompressedEndByte;

            var result = (recommendedStart, recommendedEnd);
            return result;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;

                case SeekOrigin.Current:
                    position += offset;
                    break;

                case SeekOrigin.End:
                    position = Length - offset;
                    break;
            }

            return position;
        }

        public override void SetLength(long value) => throw new NotImplementedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
    }

    public class Mapping
    {
        public long CompressedStartByte;

        public long UncompressedStartByte;
        public long UncompressedEndByte;

        public override string ToString()
        {
            var uncompressedSize = (ulong)(UncompressedEndByte - UncompressedStartByte);
            var uncompressedSizeStr = uncompressedSize.BytesToString();
            string result = $"Compressed {CompressedStartByte:N0} == Uncompressed {UncompressedStartByte:N0} ({uncompressedSizeStr} uncompressed data)";
            return result;
        }
    }
}
