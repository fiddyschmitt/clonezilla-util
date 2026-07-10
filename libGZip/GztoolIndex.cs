using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace libGZip
{
    /// <summary>
    /// One access point in a gztool index: the decompressor can resume at compressed byte
    /// <see cref="CompressedOffset"/> (bit offset <see cref="Bits"/>) producing output from
    /// <see cref="UncompressedOffset"/>, provided the preceding ≤32 KB of output (the "window",
    /// stored zlib-compressed in the index file) is preloaded as inflater history.
    /// </summary>
    public sealed class GztoolIndexPoint
    {
        public long UncompressedOffset;     //zran "out"
        public long CompressedOffset;       //zran "in": offset of the first full byte of this point
        public int Bits;                    //0, or 1-7 = that many high bits of byte at in-1 belong to this point
        public long WindowPositionInFile;   //where this point's window bytes sit in the index file
        public int WindowSize;              //stored (zlib-compressed) size of the window; 0 = no window
    }

    /// <summary>
    /// Reader for gztool's binary .gzi index format (v0 "gzipindx" / v1 "gzipindX"), as written by
    /// gztool's serialize_index_to_file(). All integers are big-endian. Windows are loaded lazily -
    /// an index over a TB-scale image has ~100k+ points, so materialising every 32 KB window would
    /// cost GBs of RAM for data that is only needed one point at a time.
    /// </summary>
    public sealed class GztoolIndex
    {
        const int WINSIZE = 32768;              //gztool/zran sliding window size

        public string Filename { get; }
        public IReadOnlyList<GztoolIndexPoint> Points { get; }
        public long UncompressedTotalLength { get; }

        GztoolIndex(string filename, List<GztoolIndexPoint> points, long uncompressedTotalLength)
        {
            Filename = filename;
            Points = points;
            UncompressedTotalLength = uncompressedTotalLength;
        }

        public static GztoolIndex Load(string filename)
        {
            using var fs = File.OpenRead(filename);

            Span<byte> header = stackalloc byte[16];
            fs.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt64BigEndian(header[..8]) != 0)
                throw new InvalidDataException("Not a gztool index (first 8 bytes are not zero).");

            var version = Encoding.ASCII.GetString(header[8..16]) switch
            {
                "gzipindx" => 0,
                "gzipindX" => 1,    //v1 adds line-number info
                _ => throw new InvalidDataException("Not a gztool index (identifier string not recognised).")
            };

            if (version == 1) ReadUInt32BE(fs);     //line_number_format

            var have = (long)ReadUInt64BE(fs);      //written only when gztool finalises the index
            _ = ReadUInt64BE(fs);                   //"size" (allocation count; not meaningful on disk)

            if (have <= 0)
                throw new InvalidDataException("gztool index is incomplete (point count not finalised).");

            var points = new List<GztoolIndexPoint>((int)have);
            for (long i = 0; i < have; i++)
            {
                var point = new GztoolIndexPoint
                {
                    UncompressedOffset = (long)ReadUInt64BE(fs),
                    CompressedOffset = (long)ReadUInt64BE(fs),
                    Bits = (int)ReadUInt32BE(fs),
                    WindowSize = (int)ReadUInt32BE(fs),
                };
                point.WindowPositionInFile = fs.Position;
                fs.Seek(point.WindowSize, SeekOrigin.Current);
                if (version == 1) ReadUInt64BE(fs); //line_number

                if (point.Bits is < 0 or > 7 || point.CompressedOffset < 0 || point.UncompressedOffset < 0)
                    throw new InvalidDataException($"gztool index point {i} has implausible values.");
                points.Add(point);
            }

            var uncompressedTotalLength = (long)ReadUInt64BE(fs);   //file_size footer
            if (uncompressedTotalLength < points[^1].UncompressedOffset)
                throw new InvalidDataException("gztool index footer (uncompressed size) is implausible.");

            return new GztoolIndex(filename, points, uncompressedTotalLength);
        }

        /// <summary>
        /// Loads and decompresses one point's window (≤32 KB of uncompressed history). Thread-safe:
        /// opens the index file per call (small reads, OS-cached).
        /// </summary>
        public byte[] LoadWindow(GztoolIndexPoint point)
        {
            if (point.WindowSize == 0) return [];

            byte[] stored;
            using (var fs = File.OpenRead(Filename))
            {
                fs.Position = point.WindowPositionInFile;
                stored = new byte[point.WindowSize];
                fs.ReadExactly(stored);
            }

            //gztool stores windows zlib-compressed; a raw (uncompressed) 32 KB window is possible in
            //some gztool code paths, recognisable by an invalid zlib header
            var looksLikeZlib = stored.Length >= 2 && (stored[0] & 0x0F) == 8 && ((stored[0] << 8 | stored[1]) % 31) == 0;
            if (!looksLikeZlib && stored.Length == WINSIZE) return stored;

            using var inflater = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
            using var result = new MemoryStream(WINSIZE);
            inflater.CopyTo(result);

            var window = result.ToArray();
            if (window.Length > WINSIZE)
                throw new InvalidDataException($"gztool index window inflated to {window.Length:N0} bytes (expected ≤ {WINSIZE:N0}).");
            return window;
        }

        static ulong ReadUInt64BE(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[8];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt64BigEndian(buffer);
        }

        static uint ReadUInt32BE(Stream stream)
        {
            Span<byte> buffer = stackalloc byte[4];
            stream.ReadExactly(buffer);
            return BinaryPrimitives.ReadUInt32BigEndian(buffer);
        }
    }
}
