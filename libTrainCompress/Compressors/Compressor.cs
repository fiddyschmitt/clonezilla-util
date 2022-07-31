using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress.Compressors
{
    public abstract class Compressor
    {
        public string CompressionFormat;
        public abstract Stream GetCompressor(Stream streamToWriteTo);

        public abstract Stream GetDecompressor(Stream streamToReadFrom);

        public Compressor(string compressionFormat)
        {
            CompressionFormat = compressionFormat;
        }
    }
}
