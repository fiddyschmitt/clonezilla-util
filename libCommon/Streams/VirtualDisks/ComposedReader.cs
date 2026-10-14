using System;
using System.Collections.Generic;
using System.Linq;

namespace libCommon.Streams.VirtualDisks
{
    /// <summary>
    /// A read-only positional source assembled from pieces at fixed offsets - fixed bytes, a range of another
    /// positional source, or bytes generated on demand - with everything between them reading as zeros. A virtual disk
    /// is a few small structures and one large payload among zeros, so this is all one needs: nothing is materialised.
    /// A read is served from one piece (or one gap) at a time; callers loop, as they do for the sources themselves.
    /// Immutable once built, so it can be read concurrently.
    /// </summary>
    public sealed class ComposedReader : IPositionalReader
    {
        /// <summary>Reads <paramref name="count"/> bytes (or fewer) at <paramref name="offset"/> within the piece.</summary>
        public delegate int PieceRead(long offset, byte[] buffer, int bufferOffset, int count);

        public sealed class Piece
        {
            public long Start { get; }
            public long Length { get; }
            public long End => Start + Length;
            internal PieceRead Read { get; }

            Piece(long start, long length, PieceRead read)
            {
                Start = start;
                Length = length;
                Read = read;
            }

            public static Piece Bytes(long start, byte[] bytes) =>
                new(start, bytes.Length, (offset, buffer, bufferOffset, count) =>
                {
                    Buffer.BlockCopy(bytes, (int)offset, buffer, bufferOffset, count);
                    return count;
                });

            public static Piece Reader(long start, IPositionalReader source, long length) =>
                new(start, length, source.ReadAt);

            public static Piece Generated(long start, long length, PieceRead generate) => new(start, length, generate);
        }

        readonly Piece[] pieces;

        public ComposedReader(long length, IEnumerable<Piece> pieces)
        {
            Length = length;
            this.pieces = pieces.Where(piece => piece.Length > 0).OrderBy(piece => piece.Start).ToArray();

            for (var i = 0; i < this.pieces.Length; i++)
            {
                var piece = this.pieces[i];
                if (piece.Start < 0 || piece.End > length) throw new ArgumentException($"A piece at {piece.Start:N0} of {piece.Length:N0} bytes lies outside the {length:N0} composed.");
                if (i > 0 && piece.Start < this.pieces[i - 1].End) throw new ArgumentException($"The pieces at {this.pieces[i - 1].Start:N0} and {piece.Start:N0} overlap.");
            }
        }

        public long Length { get; }

        public int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
            if (count <= 0 || position >= Length) return 0;
            count = (int)Math.Min(count, Length - position);

            //the last piece starting at or before the position
            int index = -1, low = 0, high = pieces.Length - 1;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                if (pieces[middle].Start <= position)
                {
                    index = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (index >= 0 && position < pieces[index].End)
            {
                var piece = pieces[index];
                return piece.Read(position - piece.Start, buffer, offset, (int)Math.Min(count, piece.End - position));
            }

            //a gap: zeros up to the next piece
            var next = index + 1 < pieces.Length ? pieces[index + 1].Start : Length;
            var zeros = (int)Math.Min(count, next - position);
            Array.Clear(buffer, offset, zeros);
            return zeros;
        }
    }
}
