using System;
using System.IO;

namespace libCommon.Streams
{
    /// <summary>
    /// A read-only pass-through view of a stream whose Dispose leaves the underlying stream open. Decoders such
    /// as GZipStream and SharpCompress's streams close their source when disposed; building them over this view
    /// lets a short-lived decoder (a format probe, a restart stream's abandoned decoder) be disposed without
    /// closing a compressed stream that is shared with other readers (gh review F40, F41).
    /// </summary>
    public sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
