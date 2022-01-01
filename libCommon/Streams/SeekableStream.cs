using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace libCommon.Streams
{
    public class SeekableStream : Stream
    {
        long position = 0;
        long? length = null;
        Stream underlyingStream;

        public SeekableStream(Func<Stream> streamFactory)
        {
            underlyingStream = streamFactory.Invoke();
            StreamFactory = streamFactory;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                if (!length.HasValue)
                {
                    length = 0;
                    var originalPosition = Position;

                    using (var stream = StreamFactory.Invoke())
                    {
                        int bufferSize = 50 * 1024 * 1024;
                        while (true)
                        {
                            var bytesRead = stream.CopyTo(Null, bufferSize, bufferSize);
                            length += bytesRead;

                            if (bytesRead == 0) break;
                        }
                    }

                    //We are now at the end of the stream. Let's go back to the original position
                    underlyingStream = StreamFactory.Invoke();
                    Seek(originalPosition, SeekOrigin.Begin);
                }

                return length.Value;
            }
        }

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public Func<Stream> StreamFactory { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesActuallyRead = underlyingStream.Read(buffer, offset, count);
            position += bytesActuallyRead;
            return bytesActuallyRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var oldPosition = position;

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

            if (position < oldPosition)
            {
                Log.Debug($"Restarting stream and seeking to correct position");

                //The original stream can't go backwards. So we need to start over
                underlyingStream = StreamFactory.Invoke();
                underlyingStream.CopyTo(Null, position, Buffers.SUPER_ARBITARY_LARGE_SIZE_BUFFER);
            }
            else
            {
                var toSeek = position - oldPosition;
                underlyingStream.CopyTo(Null, toSeek, Buffers.SUPER_ARBITARY_LARGE_SIZE_BUFFER);
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
