using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace libCommon.Streams
{
    public class SubStream : Stream
    {
        public SubStream(Stream baseStream, long startByte, long endByte)
        {
            BaseStream = baseStream;
            BaseStream.Position = startByte;

            StartByte = startByte;
            EndByte = endByte;
        }
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                var result = EndByte - StartByte;
                return result;
            }
        }

        public override long Position
        {
            get => BaseStream.Position;
            set => Seek(value, SeekOrigin.Begin);
        }
        public Stream BaseStream { get; }
        public long StartByte { get; }
        public long EndByte { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesLeft = EndByte - Position;

            var newCount = (int)Math.Min(bytesLeft, count);

            var result = BaseStream.Read(buffer, offset, newCount);
            return result;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            BaseStream.Seek(offset, origin);
            return BaseStream.Position;
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
}
