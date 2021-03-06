using libCommon.Streams;
using SharpCompress.Compressors.Xz;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
#pragma warning disable IDE1006 // Naming Styles
    public class xzDecompressor : IDecompressor
#pragma warning restore IDE1006 // Naming Styles
    {
        public xzDecompressor(Stream compressedStream)
        {
            CompressedStream = compressedStream;
        }

        public Stream CompressedStream { get; }

        public Stream? GetSeekableStream()
        {
            return null;
        }

        public Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new XZStream(CompressedStream);
            return result;
        }
    }
}
