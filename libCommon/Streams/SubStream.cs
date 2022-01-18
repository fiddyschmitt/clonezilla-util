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
            StartByte = startByte;
            EndByte = endByte;

            Seek(0, SeekOrigin.Begin);
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
            get => BaseStream.Position - StartByte;
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
            //if (Position == Length) return 0;

            var bytesLeftInVirtualFile = Length - Position;
            //var bytesLeftInBaseStream = BaseStream.Length - BaseStream.Position;

            if (Position >= BaseStream.Length)
            {
                //we are beyond the original stream. Just return blanks
                var toClear = (int)Math.Min(bytesLeftInVirtualFile, count);
                Array.Clear(buffer, offset, toClear);
                return toClear;
            }

            var toRead = count;
            toRead = (int)Math.Min(toRead, bytesLeftInVirtualFile);
            //toRead = (int)Math.Min(toRead, bytesLeftInBaseStream);

            var result = BaseStream.Read(buffer, offset, toRead);
            return result;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    BaseStream.Seek(StartByte + offset, origin);
                    break;

                case SeekOrigin.Current:
                    BaseStream.Seek(offset, origin);
                    break;

                case SeekOrigin.End:
                    BaseStream.Seek(EndByte + offset, origin);
                    break;
            }

            return Position;
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
