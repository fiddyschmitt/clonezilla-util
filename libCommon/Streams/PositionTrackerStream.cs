using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public class PositionTrackerStream : Stream
    {
        public PositionTrackerStream(Stream baseStream)
        {
            BaseStream = baseStream;
        }

        public override bool CanRead => BaseStream.CanRead;

        public override bool CanSeek => BaseStream.CanSeek;

        public override bool CanWrite => BaseStream.CanWrite;

        public override long Length => BaseStream.Length;

        long position = 0;
        public override long Position { get => position; set => throw new NotImplementedException(); }
        public Stream BaseStream { get; }

        public override void Flush() => BaseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = BaseStream.Read(buffer, offset, count);

            position += bytesRead;

            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => BaseStream.Seek(offset, origin);

        public override void SetLength(long value) => BaseStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => BaseStream.Write(buffer, offset, count);

        public override string ToString()
        {
            var result = $"Position: {Position:N0}";
            return result;
        }
    }
}
