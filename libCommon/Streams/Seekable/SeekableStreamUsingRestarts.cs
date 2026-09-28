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

        long? length = null;

        /// <summary>Invoked once if the length had to be discovered by reading the whole stream to
        /// EOF (the expensive branch below) - lets the owner persist it so later opens can pass it
        /// to the constructor and skip the full read (TEST_ANALYSIS.md #21, Lead L7).</summary>
        public Action<long>? OnLengthDiscovered { get; set; }

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
                        Extensions.CopyTo(this, Null, Buffers.ARBITRARY_SMALL_SIZE_BUFFER);   //a discard-skip is decoder-bound; a 1 MB buffer is plenty (was 50 MB of LOH per restart)
                        length = Position;
                        //We are now at the end of the stream. Let's go back to the original position
                        Seek(originalPosition, SeekOrigin.Begin);

                        OnLengthDiscovered?.Invoke(length.Value);
                    }
                }

                return length.Value;
            }
        }

        long position = 0;
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
                //The original stream can't go backwards. So we need to start over. The decoder being replaced is
                //disposed, not just dropped: it holds decoder state (native, for zstd) and buffers (gh review F41).
                //That is safe because decoders read a NonClosingStream view of the shared compressed stream, so
                //disposing one leaves the input open for its replacement.
                Log.Debug($"Restarting stream. Need to seek from beginning to position {position.BytesToString()}");
                var abandoned = underlyingStream;
                underlyingStream = StreamFactory.Invoke();
                try { abandoned.Dispose(); } catch { }
                underlyingStream.CopyTo(Null, position, Buffers.ARBITRARY_SMALL_SIZE_BUFFER);
            }
            else
            {
                var toSeek = position - oldPosition;

                if (toSeek > 0)
                {
                    Log.Debug($"Was asked to seek from position {oldPosition:N0} to {position:N0}. To get there, need to read {toSeek.BytesToString()}");

                    var seeked = underlyingStream.CopyTo(Null, toSeek, Buffers.ARBITRARY_SMALL_SIZE_BUFFER);

                    position = oldPosition + seeked;
                }
            }

            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { underlyingStream.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
}
