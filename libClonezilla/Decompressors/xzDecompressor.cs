using SharpCompress.Compressors.Xz;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class xzDecompressor : IDecompressor
    {
        public xzDecompressor(Stream compressedStream)
        {
            CompressedStream = compressedStream;
        }

        public Stream CompressedStream { get; }

        public Stream GetSeekableStream()
        {
            var result = new XZStream(CompressedStream);
            return result;
        }

        public Stream GetSequentialStream()
        {
            var result = GetSeekableStream();
            return result;
        }
    }
}
