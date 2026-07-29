using System;
using System.IO;

namespace libCommon.Streams
{
    /// <summary>
    /// A per-consumer read cursor (its own <see cref="Position"/>) over a shared
    /// <see cref="IPositionalReader"/>. Every <see cref="Read"/> becomes a positional
    /// <see cref="IPositionalReader.ReadAt"/> on the shared source, so any number of these cursors can
    /// read one source concurrently with no serializing gate - unlike a <see cref="SharedStream"/> view,
    /// whose single lock is held across the entire downstream read (and thus across the decode).
    /// The consuming code (e.g. the native 7z Seek/Position callbacks) drives this cursor's own
    /// <see cref="position"/>; the shared source is never asked to hold a position.
    /// </summary>
    public sealed class PositionalCursorStream(IPositionalReader source) : Stream
    {
        long position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = source.ReadAt(position, buffer, offset, count);
            position += n;
            return n;
        }

        public override long Length => source.Length;

        public override long Position
        {
            get => position;
            set => position = value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return position;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
