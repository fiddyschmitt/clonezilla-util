using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace libCommon.Streams.Seekable
{
    public class SeekableStreamUsingRestarts : Stream
    {
        long position = 0;
        long? length = null;
        Stream underlyingStream;

        public SeekableStreamUsingRestarts(Func<Stream> resetStream, long? length)
        {
            underlyingStream = resetStream.Invoke();
            StreamFactory = resetStream;
            this.length = length;
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
                    if (underlyingStream.CanSeek)
                    {
                        length = underlyingStream.Length;
                    }
                    else
                    {
                        var originalPosition = Position;
                        Extensions.CopyTo(underlyingStream, Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER);
                        length = Position;
                        //We are now at the end of the stream. Let's go back to the original position
                        Seek(originalPosition, SeekOrigin.Begin);
                    }
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
            if (underlyingStream.CanSeek)
            {
                var seeked = underlyingStream.Seek(offset, origin);
                position = underlyingStream.Position;
                return seeked;
            }

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
                    position = Length + offset;
                    break;
            }

            if (position < oldPosition)
            {
                //The original stream can't go backwards. So we need to start over
                Log.Debug($"Restarting stream. Need to seek from beginning to position {position.BytesToString()}");
                underlyingStream = StreamFactory.Invoke();
                underlyingStream.CopyTo(Null, position, Buffers.ARBITARY_LARGE_SIZE_BUFFER);
            }
            else
            {
                var toSeek = position - oldPosition;

                if (toSeek > 0)
                {
                    Log.Debug($"Was asked to seek from position {oldPosition:N0} to {position:N0}. To get there, need to read {toSeek.BytesToString()}");

                    /*
                    if (toSeek > Buffers.ARBITARY_HUGE_SIZE_BUFFER)
                    {
                        //Experiment

                        //7-Zip didn't like us doing this? Why? It's fine in SiphonStream
                        toSeek = Buffers.ARBITARY_HUGE_SIZE_BUFFER;
                        Log.Debug($"That's a bit far. How about a {toSeek.BytesToString()} seek instead?");
                    }
                    */

                    var seeked = underlyingStream.CopyTo(Null, toSeek, Buffers.ARBITARY_LARGE_SIZE_BUFFER);

                    position = oldPosition + seeked;
                }
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
