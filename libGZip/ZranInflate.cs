using libCommon;
using libGZip.Vendored.SharpZipLib.Zip.Compression;
using System;
using System.Buffers;
using System.IO;

namespace libGZip
{
    /// <summary>
    /// In-process zran (zlib/examples/zran.c): resumes gzip decompression at a gztool access point
    /// with no gztool subprocess, using the vendored SharpZipLib inflater (see
    /// Vendored/SharpZipLib/README.md). Resuming needs two capabilities the BCL's DeflateStream does
    /// not expose: priming the bit buffer with the point's split bits (zlib inflatePrime - a
    /// bit-shifted byte stream is NOT equivalent, as stored blocks re-align to the original byte
    /// grid) and preloading the point's ≤32 KB window as history (zlib inflateSetDictionary).
    /// </summary>
    public static class ZranInflate
    {
        /// <summary>
        /// Decodes from <paramref name="point"/>, discarding the first <paramref name="skipOutputBytes"/>
        /// of output and then filling <paramref name="buffer"/> with up to <paramref name="count"/> bytes.
        /// <paramref name="source"/> must be an independent, seekable view of the compressed stream.
        /// Returns the number of bytes written (short only at the end of the DEFLATE stream).
        /// </summary>
        public static int DecodeAt(GztoolIndex index, GztoolIndexPoint point, Stream source, long skipOutputBytes, byte[] buffer, int offset, int count)
        {
            var inflater = new Inflater(noHeader: true);

            if (point.Bits == 0)
            {
                source.Position = point.CompressedOffset;
                inflater.PrimeForResume(index.LoadWindow(point), 0, 0);
            }
            else
            {
                //the point's first bits live at the top of the byte BEFORE CompressedOffset
                source.Position = point.CompressedOffset - 1;
                var priming = source.ReadByte();
                if (priming < 0) throw new EndOfStreamException("Compressed stream ended at the access point.");
                inflater.PrimeForResume(index.LoadWindow(point), point.Bits, priming >> (8 - point.Bits));
            }

            var inBuf = ArrayPool<byte>.Shared.Rent(Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
            var discardBuf = skipOutputBytes > 0 ? ArrayPool<byte>.Shared.Rent(Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER) : null;
            try
            {
                var sourceExhausted = false;
                var discarded = 0L;
                var produced = 0;

                while (produced < count && !inflater.IsFinished)
                {
                    if (inflater.IsNeedingInput)
                    {
                        if (sourceExhausted) break;
                        var read = source.Read(inBuf, 0, inBuf.Length);
                        if (read == 0) { sourceExhausted = true; continue; }
                        inflater.SetInput(inBuf, 0, read);
                    }

                    int got;
                    if (discarded < skipOutputBytes)
                    {
                        got = inflater.Inflate(discardBuf!, 0, (int)Math.Min(discardBuf!.Length, skipOutputBytes - discarded));
                        discarded += got;
                    }
                    else
                    {
                        got = inflater.Inflate(buffer, offset + produced, count - produced);
                        produced += got;
                    }
                }

                return produced;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(inBuf);
                if (discardBuf != null) ArrayPool<byte>.Shared.Return(discardBuf);
            }
        }
    }
}
