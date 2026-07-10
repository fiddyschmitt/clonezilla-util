using System;
using System.Collections.Generic;
using System.IO;

namespace libCommon.Streams
{
    /// <summary>
    /// Owns the sharing rules for one seekable base stream: any number of independent read cursors
    /// can be minted with <see cref="CreateView"/>. Each view keeps its own position, and the base
    /// stream's position is only ever touched inside a private gate, so views can be read
    /// concurrently without corrupting each other's progress. Callers never see the gate object or
    /// the view class - the sharing contract is correct by construction.
    /// (Replaces the old IndependentStream pattern, where every call site had to know the shared
    /// lock object and wrapper class; also drops the redundant Stream.Synchronized layer it carried -
    /// the gate already serialises every touch of the base stream, including Length.)
    /// </summary>
    public sealed class SharedStream(Stream baseStream)
    {
        readonly object gate = new();

        /// <summary>An independent read cursor over the shared base stream.</summary>
        public Stream CreateView() => new SharedStreamView(baseStream, gate);

        /// <summary>
        /// A read-only cursor over the shared stream. The base stream's position is set and used
        /// only inside the shared gate, so any number of sibling views can read concurrently.
        /// </summary>
        sealed class SharedStreamView(Stream source, object gate) : Stream
        {
            long position;

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (gate)
                {
                    source.Position = position;
                    var read = source.Read(buffer, offset, count);
                    position += read;
                    return read;
                }
            }

            public override long Length
            {
                get { lock (gate) return source.Length; }
            }

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
}
