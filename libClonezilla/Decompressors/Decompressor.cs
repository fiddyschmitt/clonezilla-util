using libCommon;
using libCommon.Streams;
using libPartclone;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ZstdNet;

namespace libClonezilla.Decompressors
{
    public abstract class Decompressor
    {
        public Stream CompressedStream { get; }

        //Returns a new decoder that its caller owns and should dispose. Implementations build it over a
        //NonClosingStream view of CompressedStream, which other readers share, so disposing a decoder never closes
        //that stream (gh review F40, F41).
        public abstract Stream GetSequentialStream();
        public abstract Stream? GetSeekableStream();

        public Decompressor(Stream compressedStream)
        {
            CompressedStream = compressedStream;
        }

        public static Compression GetCompressionType(Stream compressedStream)
        {
            Compression result = Compression.None;

            //The probes are disposed (they hold buffers, and zstd a native context), but over a view that leaves the
            //caller's stream open (gh review F40).
            var probeSource = new NonClosingStream(compressedStream);
            var decompressors = new (Compression Compression, Func<Stream> Stream)[]
            {
                (Compression.bzip2, () => BZip2Stream.Create(probeSource, SharpCompress.Compressors.CompressionMode.Decompress, false)),
                (Compression.Gzip, () => new GZipStream(probeSource, CompressionMode.Decompress)),
                (Compression.xz, () => new XZStream(probeSource)),
                (Compression.Zstandard, () => new DecompressionStream(probeSource)),
            };

            foreach (var decompressor in decompressors)
            {
                compressedStream.Seek(0, SeekOrigin.Begin);

                Stream? probe = null;
                try
                {
                    //extract a little and see what happens
                    probe = decompressor.Stream();
                    probe.CopyTo(Stream.Null, 32, 32);
                    result = decompressor.Compression;
                }
                catch { }
                finally
                {
                    try { probe?.Dispose(); } catch { }
                }

                if (result != Compression.None) break;
            }

            compressedStream.Seek(0, SeekOrigin.Begin);

            return result;
        }
    }

    public enum Compression
    {
        bzip2,
        Gzip,
        LZ4,
        LZip,
        None,        
        xz,
        Zstandard
    }
}
