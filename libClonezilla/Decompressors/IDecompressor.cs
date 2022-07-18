using libCommon;
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
    public interface IDecompressor
    {
        public Stream GetSequentialStream();
        public Stream? GetSeekableStream();

        public static Compression GetCompressionType(Stream compressedStream)
        {
            Compression result = Compression.None;

            var decompressors = new (Compression Compression, Func<Stream> Stream)[]
            {
                (Compression.Gzip, () => new GZipStream(compressedStream, CompressionMode.Decompress)),
                (Compression.Zstandard, () => new DecompressionStream(compressedStream)),
                (Compression.xz, () => new XZStream(compressedStream)),
                (Compression.bzip2, () => new BZip2Stream(compressedStream, SharpCompress.Compressors.CompressionMode.Decompress, false))
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
        Gzip,
        Zstandard,
        xz,
        bzip2,
        None
    }
}
