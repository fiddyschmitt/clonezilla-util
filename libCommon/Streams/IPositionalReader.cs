namespace libCommon.Streams
{
    /// <summary>
    /// A read-only source that supports absolute positional reads with no shared cursor. Because each
    /// <see cref="ReadAt"/> carries its own position, callers never contend on a <see cref="System.IO.Stream.Position"/>,
    /// so one instance can be read concurrently by many threads. Streams that implement this can be
    /// shared across parallel readers without a serializing lock around seek+read.
    /// </summary>
    public interface IPositionalReader
    {
        /// <summary>
        /// Reads up to <paramref name="count"/> bytes into <paramref name="buffer"/> at
        /// <paramref name="offset"/>, starting at absolute <paramref name="position"/> in the logical
        /// stream. May return fewer than requested (the caller loops); returns 0 at end of stream.
        /// Safe to call concurrently.
        /// </summary>
        int ReadAt(long position, byte[] buffer, int offset, int count);

        /// <summary>The logical length of the stream. Invariant for the read-only sources on this path.</summary>
        long Length { get; }
    }
}
