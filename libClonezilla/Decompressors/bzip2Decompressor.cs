using libCommon;
using Serilog;
using SharpCompress.Compressors.BZip2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class bzip2Decompressor : IDecompressor
    {
        public Stream CompressedStream { get; }

        public bzip2Decompressor(Stream compressedStream)
        {
            CompressedStream = compressedStream;
        }

        public Stream? GetSeekableStream()
        {
            return null;
        }

        public Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new BZip2Stream(CompressedStream, SharpCompress.Compressors.CompressionMode.Decompress, false);
            return result;
        }
    }
}
