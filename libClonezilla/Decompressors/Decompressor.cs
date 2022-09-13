using libCommon;
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

        public abstract Stream GetSequentialStream();
        public abstract Stream? GetSeekableStream();

        public Decompressor(Stream compressedStream)
        {
            CompressedStream = compressedStream;
        }

        public static Compression GetCompressionType(Stream compressedStream)
        {
            Compression result = Compression.None;

            var decompressors = new (Compression Compression, Func<Stream> Stream)[]
            {
                (Compression.bzip2, () => new BZip2Stream(compressedStream, SharpCompress.Compressors.CompressionMode.Decompress, false)),
                (Compression.Gzip, () => new GZipStream(compressedStream, CompressionMode.Decompress)),
                (Compression.xz, () => new XZStream(compressedStream)),
                (Compression.Zstandard, () => new DecompressionStream(compressedStream)),
            };

            foreach (var decompressor in decompressors)
            {
                compressedStream.Seek(0, SeekOrigin.Begin);

                try
                {
                    //extract a little and see what happens
                    decompressor.Stream().CopyTo(Stream.Null, 32, 32);
                    result = decompressor.Compression;
                    break;
                }
                catch { }
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
