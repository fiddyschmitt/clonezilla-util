using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Diagnostics;

namespace libCommon.Streams
{
    public class Multistream : Stream
    {
        long position = 0;

        public Multistream(IEnumerable<Stream> substreams)
        {
            long sizeSum = 0;
            Substreams = substreams
                                .Select((stream, index) => new Substream(
                                    sizeSum,
                                    sizeSum += stream.Length,
                                    stream,
                                    index))
                                .ToList();
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                var result = Substreams.Sum(substream => substream.Length);
                return result;
            }
        }

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public List<Substream> Substreams { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        int currentIndex = -1;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = 0;

            while (true)
            {
                var substream = Substreams.FirstOrDefault(s => Position >= s.Start && Position < s.End);

                if (substream == null) break;

                if (substream.StreamIndex != currentIndex)
                {
                    currentIndex = substream.StreamIndex;
                }

                //determine where we should start reading in the substream
                var positionInSubstream = Position - substream.Start;
                substream.Stream.Seek(positionInSubstream, SeekOrigin.Begin);

                var bytesToRead = count - bytesRead;

                var bytesActuallyRead = substream.Stream.Read(buffer, bytesRead, bytesToRead);

                bytesRead += bytesActuallyRead;
                position += bytesActuallyRead;

                if (bytesRead >= count) break;
            }

            if (bytesRead > count)
            {
                throw new Exception($"Read too many bytes! Should have read {count:N0} but read {bytesRead:N0}");
            }

            return bytesRead;
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

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }

    public class Substream
    {
        public long Start;
        public long End;
        public long Length => End - Start;
        public Stream Stream;
        public int StreamIndex;

        public Substream(long start, long end, Stream stream, int streamIndex)
        {
            Start = start;
            End = end;
            Stream = stream;
            StreamIndex = streamIndex;
        }
    }
}
