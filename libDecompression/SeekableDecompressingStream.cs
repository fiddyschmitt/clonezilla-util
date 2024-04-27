using libCommon;
using libCommon.Streams;
using libDecompression.Lists;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDecompression
{
    public abstract class SeekableDecompressingStream : Stream, IReadSuggestor
    {
        public abstract IList<Mapping> Blocks { get; }

        //long UncompressedTotalLength => Blocks.Last().UncompressedEndByte;
        public abstract long UncompressedTotalLength { get; }

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
            var chunkDetails = Blocks.BinarySearch(position, MappingComparer);

            if (chunkDetails == null)
            {
                return 0;
            }

            var bytesLeftInFile = Length - position;
            var bytesToRead = (int)Math.Min(count, bytesLeftInFile);

            Log.Debug($"Attempting to read {count.BytesToString()} from position {Position:N0}");
            var startTime = DateTime.Now;
            var bytesRead = ReadFromChunk(chunkDetails, buffer, offset, bytesToRead);
            Log.Debug($"Finished reading. Mananaged {bytesRead.BytesToString()} in {(DateTime.Now - startTime).TotalSeconds:N2} seconds");

            position += bytesRead;

            return bytesRead;
        }

        public (long Start, long End) GetRecommendation(long start)
        {
            var startIndexPoint = Blocks.BinarySearch(start + 1, MappingComparer) ?? throw new Exception($"Could not find block which contains position {start:N0}");

            return (startIndexPoint.UncompressedStartByte, startIndexPoint.UncompressedEndByte);
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
