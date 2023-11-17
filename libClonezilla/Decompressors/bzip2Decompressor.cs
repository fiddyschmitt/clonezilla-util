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
    public class Bzip2Decompressor : Decompressor
    {
        public Bzip2Decompressor(Stream compressedStream) : base(compressedStream)
        {

        }

        public override Stream? GetSeekableStream()
        {
            return null;
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new BZip2Stream(CompressedStream, SharpCompress.Compressors.CompressionMode.Decompress, false);
            return result;
        }
    }
}
