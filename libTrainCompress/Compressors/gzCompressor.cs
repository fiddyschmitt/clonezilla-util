using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress.Compressors
{
    public class gzCompressor : Compressor
    {
        public gzCompressor() : base("gz")
        {
        }

        public override Stream GetCompressor(Stream streamToWriteTo)
        {
            var result = new GZipStream(streamToWriteTo, CompressionMode.Compress, true);
            return result;
        }


        public override Stream GetDecompressor(Stream streamToReadFrom)
        {
            var result = new GZipStream(streamToReadFrom, CompressionMode.Decompress, true);
            return result;
        }
    }
}
