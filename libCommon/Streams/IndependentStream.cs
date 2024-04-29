using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    //Independantly uses the provided the stream, keeping track of its on Position and uses the underlying stream in a synchronised manner
    public class IndependentStream(Stream baseStream, object readLock) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => BaseStream.Length;

        long positionInThisVirtualStream = 0;
        public override long Position
        {
            get
            {
                return positionInThisVirtualStream;
            }

            set
            {
                lock (ReadLock)
                {
                    Seek(value, SeekOrigin.Begin);
                }
            }
        }
        public Stream BaseStream { get; } = Synchronized(baseStream);
        public object ReadLock { get; } = readLock;

        public override void Flush() => BaseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (ReadLock)
            {
                //make sure the base stream is where we expect it to be
                BaseStream.Seek(positionInThisVirtualStream, SeekOrigin.Begin);

                var toRead = count;

                var bytesRead = BaseStream.Read(buffer, offset, toRead);

                positionInThisVirtualStream = BaseStream.Position;

                return bytesRead;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (ReadLock)
            {
                long newAbsolutePosition = 0;

                switch (origin)
                {
                    case SeekOrigin.Begin:
                        newAbsolutePosition = offset;
                        break;

                    case SeekOrigin.Current:
                        newAbsolutePosition = positionInThisVirtualStream + offset;
                        break;

                    case SeekOrigin.End:
                        newAbsolutePosition = Length + offset;
                        break;
                }

                BaseStream.Seek(newAbsolutePosition, SeekOrigin.Begin);

                positionInThisVirtualStream = BaseStream.Position;
                return positionInThisVirtualStream;
            }
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
