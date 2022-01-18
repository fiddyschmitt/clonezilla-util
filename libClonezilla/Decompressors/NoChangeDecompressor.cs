using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class NoChangeDecompressor : IDecompressor
    {
        public NoChangeDecompressor(Stream compressedStream)
        {
            CompressedStream = compressedStream;
        }

        public Stream CompressedStream { get; }

        public Stream GetSeekableStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            return CompressedStream;
        }

        public Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            return CompressedStream;
        }
    }
}
