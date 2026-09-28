using libCommon.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class NoChangeDecompressor : Decompressor
    {
        public NoChangeDecompressor(Stream compressedStream) : base(compressedStream)
        {
        }

        public override Stream? GetSeekableStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            return CompressedStream;
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            return new NonClosingStream(CompressedStream);   //a consumer disposing its "decoder" must not close the shared file
        }
    }
}
