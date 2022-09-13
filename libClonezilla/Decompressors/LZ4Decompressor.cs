using K4os.Compression.LZ4.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class LZ4Decompressor : Decompressor
    {
        public LZ4Decompressor(Stream compressedStream) : base(compressedStream)
        {
        }

        public override Stream? GetSeekableStream()
        {
            return null;
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = LZ4Stream.Decode(CompressedStream, null, true, false);
            return result;
        }
    }
}
