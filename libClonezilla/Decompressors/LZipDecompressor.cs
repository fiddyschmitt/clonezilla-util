using K4os.Compression.LZ4.Streams;
using SharpCompress.Compressors.LZMA;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class LZipDecompressor : Decompressor
    {
        public LZipDecompressor(Stream compressedStream) : base(compressedStream)
        {
        }

        public override Stream? GetSeekableStream()
        {
            return null;
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new LZipStream(CompressedStream, SharpCompress.Compressors.CompressionMode.Decompress);
            return result;
        }
    }
}
