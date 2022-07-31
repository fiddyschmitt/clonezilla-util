using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress.Compressors
{
    public class zstdCompressor : Compressor
    {
        public zstdCompressor() : base("zstd")
        {
        }

        public override Stream GetCompressor(Stream streamToWriteTo)
        {
            var result = new ZstdNet.CompressionStream(streamToWriteTo);
            return result;
        }

        public override Stream GetDecompressor(Stream streamToReadFrom)
        {
            var result = new ZstdNet.DecompressionStream(streamToReadFrom);
            return result;
        }
    }
}
